
namespace GWallet.Backend

open NBitcoin

type PublicKey private (pubKey: string, currencyOpt: Option<Currency>) =
    do
        match currencyOpt with
        | Some currency when currency.IsUtxo() ->
            // just for validation
            PubKey pubKey |> ignore
        | _ ->
            // TODO: validate ether keys too
            ()

    internal new(pubKey: PubKey) = PublicKey(pubKey.ToString(), None)
    internal new(pubKey: string, currency: Currency) = PublicKey(pubKey.ToString(), Some currency)

    override __.ToString() =
        pubKey

    static member internal Parse (currency: Currency) (pubKey: string): PublicKey =
        PublicKey (pubKey, currency)

