namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.Net
open System.Net.Sockets

open NBitcoin

open DotNetLightning.Serialize.Msgs
open DotNetLightning.Utils

open GWallet.Backend
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks
open GWallet.Backend.UtxoCoin.Lightning.Primitives
open FSharp.Core

type PeerErrorMessage = {
    ErrorMessage: ErrorMessage
} with
    member this.Message =
        if this.ErrorMessage.Data.Length = 1 then
            let code = this.ErrorMessage.Data.[0]
            (SPrintF1 "Error code %i received from lightning peer: " code) +
            match code with
            | 0x01uy ->
                "The number of pending channels exceeds the policy limit.\n\
                Hint: You can try from a new node identity."
            | 0x02uy ->
                "Node is not synced to blockchain." +
                if Config.BitcoinNet = Network.RegTest then
                    "\nHint: Try mining some blocks before opening."
                else
                    String.Empty
            | 0x03uy ->
                "Channel capacity too large.\n\
                Hint: Try with a smaller funding amount."
            | _ ->
                "(unknown error code)"
        else
            System.Text.ASCIIEncoding.ASCII.GetString this.ErrorMessage.Data

type RecvChannelMsgError =
    | RecvMsg of RecvMsgError
    | ReceivedPeerErrorMessage of PeerWrapper * PeerErrorMessage
    with
    member this.Message =
        match this with
        | RecvMsg err ->
            SPrintF1 "Error receiving message from peer: %s" err.Message
        | ReceivedPeerErrorMessage (_, err) ->
            SPrintF1 "Error message from peer: %s" err.Message

and PeerWrapper = {
    Init: Init
    MsgStream: MsgStream
} with
    interface IDisposable with
        member this.Dispose() =
            (this.MsgStream :> IDisposable).Dispose()

    static member Connect (nodeSecret: ExtKey)
                          (peerNodeId: NodeId)
                          (peerId: PeerId)
                              : Async<Result<PeerWrapper, ConnectError>> = async {
        let! connectRes = MsgStream.Connect nodeSecret peerNodeId peerId
        match connectRes with
        | Error connectError -> return Error connectError
        | Ok (init, msgStream) ->
            return Ok {
                Init = init
                MsgStream = msgStream
            }
    }

    static member AcceptFromTransportListener (transportListener: TransportListener)
                                              (peerNodeId: NodeId)
                                                  : Async<Result<PeerWrapper, ConnectError>> = async {
        let! acceptRes = MsgStream.AcceptFromTransportListener transportListener
        match acceptRes with
        | Error connectError -> return Error connectError
        | Ok (init, msgStream) ->
            if msgStream.RemoteNodeId = peerNodeId then
                return Ok {
                    Init = init
                    MsgStream = msgStream
                }
            else
                (msgStream :> IDisposable).Dispose()
                return! PeerWrapper.AcceptFromTransportListener transportListener peerNodeId
    }

    static member AcceptAnyFromTransportListener (transportListener: TransportListener)
                                                     : Async<Result<PeerWrapper, ConnectError>> = async {
        let! acceptRes = MsgStream.AcceptFromTransportListener transportListener
        match acceptRes with
        | Error connectError -> return Error connectError
        | Ok (init, msgStream) ->
            return Ok {
                Init = init
                MsgStream = msgStream
            }
    }

    member this.RemoteNodeId: NodeId =
        this.MsgStream.RemoteNodeId

    member this.PeerId: PeerId =
        this.MsgStream.PeerId

    member this.RemoteEndPoint: IPEndPoint =
        this.MsgStream.RemoteEndPoint

    member this.LnEndPoint: LnEndPoint =
        this.MsgStream.LnEndPoint

    member this.NodeSecret: ExtKey =
        this.MsgStream.NodeSecret

    member this.SendMsg (msg: ILightningMsg): Async<PeerWrapper> = async {
        let! msgStream = this.MsgStream.SendMsg msg
        return { this with MsgStream = msgStream }
    }

    member this.RecvChannelMsg(): Async<Result<PeerWrapper * IChannelMsg, RecvChannelMsgError>> =
        let rec recv (msgStream: MsgStream) = async {
            let! recvMsgRes = msgStream.RecvMsg()
            match recvMsgRes with
            | Error recvMsgError -> return Error <| RecvMsg recvMsgError
            | Ok (msgStreamAfterMsgReceived, msg) ->
                match msg with
                | :? ErrorMessage as errorMessage ->
                    let peerWrapper = { this with MsgStream = msgStreamAfterMsgReceived }
                    return Error <| ReceivedPeerErrorMessage (peerWrapper, { ErrorMessage = errorMessage })
                | :? Ping as pingMsg ->
                    let! msgStreamAfterPongSent = msgStreamAfterMsgReceived.SendMsg { Pong.BytesLen = pingMsg.PongLen }
                    return! recv msgStreamAfterPongSent
                | :? Pong ->
                    return failwith "sending pings is not implemented"
                | :? Init ->
                    return failwith "unexpected init msg"
                | :? IRoutingMsg ->
                    Infrastructure.LogDebug "handling routing messages is not implemented"
                    return! recv msgStreamAfterMsgReceived
                | :? IChannelMsg as msg ->
                    let peerWrapper = { this with MsgStream = msgStreamAfterMsgReceived }
                    return Ok (peerWrapper, msg)
                | _ ->
                    return failwith <| SPrintF1 "unreachable %A" msg
        }
        recv this.MsgStream


