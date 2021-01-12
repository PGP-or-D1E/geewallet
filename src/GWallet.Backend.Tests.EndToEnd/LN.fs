namespace GWallet.Backend.Tests.EndToEnd

open Microsoft.FSharp.Linq.NullableOperators // For <>?

open Newtonsoft.Json // For JsonConvert

open NUnit.Framework

open GWallet.Backend

open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND

open System
open System.IO // For File.WriteAllText
open System.Diagnostics // For Process
open System.Net // For IPAddress and IPEndPoint
open System.Text // For Encoding
open System.Threading // For AutoResetEvent and CancellationToken
open System.Threading.Tasks // For Task
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq

open NBitcoin // For ExtKey

open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open ResultUtils.Portability
open GWallet.Backend.UtxoCoin // For NormalUtxoAccount
open GWallet.Backend.UtxoCoin.Lightning
open GWallet.Backend.FSharpUtil
open GWallet.Backend.FSharpUtil.UwpHacks

type ProcessWrapper = {
    Name: string
    Process: Process
    Queue: ConcurrentQueue<string>
    Semaphore: Semaphore
    OutputFileStream: StreamWriter
} with
    interface IDisposable with
        member this.Dispose() =
            (this.OutputFileStream :> IDisposable).Dispose()
            (this.Process :> IDisposable).Dispose()

    static member New (name: string)
                      (workDir: string)
                      (arguments: string)
                      (environment: Map<string, string>)
                      (isPython: bool)
                          : ProcessWrapper =
        let timestamp() =
            // NOTE: this must be the same format used in scripts/make.fsx
            let dateTimeFormat = "yyyy-MM-dd:HH:mm:ss.ffff"
            DateTime.Now.ToString(dateTimeFormat)
        let outputFileStream =
            let outputFileName =
                let rand = new Random()
                // NOTE: the file name must end in .????????.log for the sake of scripts/make.fsx
                SPrintF3 "%s/%s.%s.log" workDir name (rand.Next().ToString("x8"))
            Infrastructure.LogDebug (SPrintF1 "Starting subprocess: %s" name)
            File.CreateText outputFileName
        outputFileStream.WriteLine(SPrintF1 "%s: <started>" (timestamp()))
        let fileName =
            let environmentPath = System.Environment.GetEnvironmentVariable "PATH"
            let pathSeparator = Path.PathSeparator
            let paths = environmentPath.Split pathSeparator
            let isWin = Path.DirectorySeparatorChar = '\\'
            let exeName =
                if isWin then
                    name + if isPython then ".py" else ".exe"
                else
                    name
            let paths = [ for x in paths do yield Path.Combine(x, exeName) ]
            let matching = paths |> List.filter File.Exists
            match matching with
            | first :: _ -> first
            | _ ->
                failwith <|
                    SPrintF3
                        "Couldn't find %s in path, tried %A, these paths matched: %A"
                        exeName
                        [ for x in paths do yield (File.Exists x, x) ]
                        matching

        let queue = ConcurrentQueue()
        let semaphore = new Semaphore(0, Int32.MaxValue)
        let startInfo =
            ProcessStartInfo (
                UseShellExecute = false,
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )
        for kvp in environment do
            startInfo.Environment.[kvp.Key] <- kvp.Value
        let proc = new Process()
        proc.StartInfo <- startInfo
        let firstStreamEnded = ref false
        let outputHandler (_: obj) (args: DataReceivedEventArgs) =
            lock firstStreamEnded <| fun () ->
                match args.Data with
                | null ->
                    // We need to wait for both streams (stdout and stderr) to
                    // end. So output has ended and the process has exited
                    // after the second null.
                    if not !firstStreamEnded then
                        firstStreamEnded := true
                    else
                        outputFileStream.WriteLine(SPrintF1 "%s: <exited>" (timestamp()))
                        outputFileStream.Flush()
                        semaphore.Release() |> ignore
                | text ->
                    outputFileStream.WriteLine(SPrintF2 "%s: %s" (timestamp()) text)
                    outputFileStream.Flush()
                    queue.Enqueue text
                    semaphore.Release() |> ignore
        proc.OutputDataReceived.AddHandler(DataReceivedEventHandler outputHandler)
        proc.ErrorDataReceived.AddHandler(DataReceivedEventHandler outputHandler)
        proc.EnableRaisingEvents <- true
        if not(proc.Start()) then
            failwith "failed to start process"
        AppDomain.CurrentDomain.ProcessExit.AddHandler(EventHandler (fun _ _ -> proc.Close()))
        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()
        {
            Name = name
            Process = proc
            Queue = queue
            Semaphore = semaphore
            OutputFileStream = outputFileStream
        }

    member this.WaitForMessage(msgFilter: string -> bool) =
        this.Semaphore.WaitOne() |> ignore
        let running, line = this.Queue.TryDequeue()
        if running then
            if msgFilter line then
                ()
            else
                this.WaitForMessage msgFilter
        else
            failwith (this.Name + " exited without outputting message")

    member this.WaitForExit() =
        this.Semaphore.WaitOne() |> ignore
        let running, _ = this.Queue.TryDequeue()
        if running then
            this.WaitForExit()

    member this.ReadToEnd(): list<string> =
        let rec fold (lines: list<string>) =
            this.Semaphore.WaitOne() |> ignore
            let running, line = this.Queue.TryDequeue()
            if running then
                fold <| List.append lines [line]
            else
                lines
        fold List.empty

type EstimateSmartFeeResponse = {
    feerate: decimal
    errors: list<string>
    blocks: uint64
}

type UnspentResponse = {
    txid: string
    vout: uint32
    amount: decimal
}

type MempoolInfo = {
    TransactionCount: uint32
    TotalWeight: uint32
    MemoryUsage: uint64
    MaxMemoryUsage: uint64
    MinFee: FeeRatePerKw
    MinRelayFee: FeeRatePerKw
}

type GetMempoolInfoResponse = {
    size: uint32
    bytes: decimal
    usage: uint64
    maxmempool: uint64
    mempoolminfee: decimal
    minrelaytxfee: decimal
}

type SignRawTransactionWithWalletError = {
    error: string
}

type SignRawTransactionWithWalletResponse = {
    hex: string
    complete: bool
    errors: list<SignRawTransactionWithWalletError>
}

type Bitcoind = {
    WorkDir: string
    DataDir: string
    RpcUser: string
    RpcPassword: string
    ProcessWrapper: ProcessWrapper
} with
    interface IDisposable with
        member this.Dispose() =
            this.ProcessWrapper.Process.Kill()
            this.ProcessWrapper.WaitForExit()
            (this.ProcessWrapper :> IDisposable).Dispose()
            Directory.Delete(this.DataDir, true)

    static member Start(): Bitcoind =
        let workDir = TestContext.CurrentContext.WorkDirectory
        let dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory dataDir |> ignore
        let rpcUser = Path.GetRandomFileName()
        let rpcPassword = Path.GetRandomFileName()
        let confPath = Path.Combine(dataDir, "bitcoin.conf")
        let defaultFeeRate = FeeRatePerKw 5000u
        (*
        let fakeFeeRate = !UtxoCoin.ElectrumClient.RegTestFakeFeeRate
        File.WriteAllText(
            confPath,
            SPrintF3
                "\
                txindex=1\n\
                printtoconsole=1\n\
                rpcuser=%s\n\
                rpcpassword=%s\n\
                rpcallowip=127.0.0.1\n\
                zmqpubrawblock=tcp://127.0.0.1:28332\n\
                zmqpubrawtx=tcp://127.0.0.1:28333\n\
                fallbackfee=%f\n\
                [regtest]\n\
                rpcbind=127.0.0.1\n\
                rpcport=18554"
                rpcUser
                rpcPassword
                fakeFeeRate
        )
        *)
        File.WriteAllText(
            confPath,
            SPrintF2
                "\
                txindex=1\n\
                printtoconsole=1\n\
                rpcuser=%s\n\
                rpcpassword=%s\n\
                rpcallowip=127.0.0.1\n\
                zmqpubrawblock=tcp://127.0.0.1:28332\n\
                zmqpubrawtx=tcp://127.0.0.1:28333\n\
                [regtest]\n\
                rpcbind=127.0.0.1\n\
                rpcport=18554"
                rpcUser
                rpcPassword
        )

        let processWrapper =
            ProcessWrapper.New
                "bitcoind"
                workDir
                (SPrintF1 "-regtest -datadir=%s -acceptnonstdtxn" dataDir)
                Map.empty
                false
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "init message: Done loading")
        let ret = {
            WorkDir = workDir
            DataDir = dataDir
            RpcUser = rpcUser
            RpcPassword = rpcPassword
            ProcessWrapper = processWrapper
        }
        let selfAddress = ret.GetNewAddress()
        ret.GenerateBlocksRaw
            // TODO: Why does this have to be so high?  I think because it
            // needs lots of txouts to use. Try to first mine a tx that splits
            // our funds into a whole bunch of txouts and see if we can then
            // reduce this to something sane.
            (BlockHeightOffset32 (550u + uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity))
            //(BlockHeightOffset32 (55u + uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity))
            selfAddress
        ret.SetFeeRateByMining defaultFeeRate
        ret

    //member private this.RunCommand<'R> (): 'R =


    member this.GetMempoolInfo (): MempoolInfo =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getmempoolinfo" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let response = JsonConvert.DeserializeObject<GetMempoolInfoResponse> output
        {
            TransactionCount = response.size
            TotalWeight = uint32 (4m * response.bytes)
            MemoryUsage = response.usage
            MaxMemoryUsage = response.maxmempool
            MinFee =
                let btcPerKB = response.mempoolminfee
                let satPerKB = (Money (btcPerKB, MoneyUnit.BTC)).ToUnit MoneyUnit.Satoshi
                // 4 weight units per byte. See segwit specs.
                let kwPerKB = 4m
                let satPerKw = satPerKB / kwPerKB
                let feeRatePerKw = FeeRatePerKw (uint32 satPerKw)
                feeRatePerKw
            MinRelayFee =
                let btcPerKB = response.minrelaytxfee
                let satPerKB = (Money (btcPerKB, MoneyUnit.BTC)).ToUnit MoneyUnit.Satoshi
                // 4 weight units per byte. See segwit specs.
                let kwPerKB = 4m
                let satPerKw = satPerKB / kwPerKB
                let feeRatePerKw = FeeRatePerKw (uint32 satPerKw)
                feeRatePerKw
        }

    (*
    member this.GetBlockHeight (): BlockHeight =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getblockcount" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        //Console.WriteLine(sprintf "getblockcount gave us: %s" output)
        let height = JsonConvert.DeserializeObject<uint32> output
        BlockHeight height
    *)

    (*
    member this.GetBlockHash (blockHeight: BlockHeight): uint256 =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s getblockhash %i" this.DataDir blockHeight.Value)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        uint256 output
    *)

    (*
    member this.GetBlock (blockHash: uint256): Block =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s getblock %s 2" this.DataDir (blockHash.ToString()))
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        //Console.WriteLine(sprintf "getblock gave us: %s" output)
        Block.Parse(output, Network.RegTest)
    *)

    member this.SetFeeRateForBitcoindTransactions (feeRate: FeeRatePerKw): unit =
        let feeRateDecimal =
            let satPerKw = decimal feeRate.Value
            let kwPerKB = 4m
            let satPerKB = satPerKw * kwPerKB
            let btcPerKB = (Money (satPerKB, MoneyUnit.Satoshi)).ToUnit MoneyUnit.BTC
            btcPerKB
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s settxfee %M" this.DataDir feeRateDecimal)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let success = JsonConvert.DeserializeObject<bool> output
        assert success

    member this.SetFeeRateByMining (feeRate: FeeRatePerKw): unit =
        let address = this.GetNewAddress()
        let rec setFeeRate (attemptNum: int) =
            if attemptNum >= 20 then
                failwith "Setting fee rate by mining isn't working for some reason..."
            let currentFeeRate = this.EstimateSmartFee (BlockHeightOffset16 6us)
            Console.WriteLine(sprintf "FEERATE: target == %A, current == %A" feeRate currentFeeRate)
            match currentFeeRate with
            | Some currentFeeRate when currentFeeRate >= feeRate -> ()
            | _ ->
                this.GenerateBlocksWithFeeRate (BlockHeightOffset32 1u) address feeRate
                setFeeRate (attemptNum + 1)
        setFeeRate 0

    member this.SignRawTransactionWithWallet (transaction: Transaction): Transaction =
        //Console.WriteLine(sprintf "in SignRawTransactionWithWallet")
        //Console.WriteLine(sprintf "transaction == %A" transaction)
        let transactionHex = transaction.ToHex()
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s signrawtransactionwithwallet %s" this.DataDir transactionHex)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        //Console.WriteLine(sprintf "output == %s" output)
        let response = JsonConvert.DeserializeObject<SignRawTransactionWithWalletResponse> output
        //Console.WriteLine(sprintf "response == %A" response)
        if response.complete && (Object.ReferenceEquals(response.errors, null) || response.errors.Length = 0) then
            Transaction.Parse(response.hex, Network.RegTest)
        else
            failwith "signrawtransactionwithwallet failed"

    member this.TrySendRawTransaction (transaction: Transaction): Option<TxId> =
        let transactionHex = transaction.ToHex()
        //Console.WriteLine(sprintf "sending raw transaction: %s" transactionHex)
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s sendrawtransaction %s 0" this.DataDir transactionHex)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = (String.concat "\n" lines).Trim()
        //Console.WriteLine(sprintf "sendrawtransaction gave us: %s" output)
        if output.StartsWith "error code" then
            None
        else
            output |> uint256 |> TxId |> Some

    member this.ListUnspent (): list<Coin> =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s listunspent" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let unspentList = JsonConvert.DeserializeObject<list<UnspentResponse>> output
        //Console.WriteLine(sprintf "UNSPENT = %A" unspentList)
        List.map
            (fun (unspent: UnspentResponse) ->
                Coin(
                    uint256 unspent.txid,
                    unspent.vout,
                    Money(unspent.amount, MoneyUnit.BTC),
                    Script.Empty
                )
            )
            unspentList

    //member this.TrySendFatTx (amount: Money) (size: uint32) (feeRate: FeeRatePerKw): Option<TxId> =
    member this.TrySendFatTx (unspentCoins: list<Coin>) (amount: Money) (weight: uint32) (feeRate: FeeRatePerKw): Option<list<Coin>> =
        let amountWithFeeOverEstimate =
            let minWeight = 1000UL
            let feeOverEstimate = feeRate.CalculateFeeFromWeight (minWeight + uint64 weight)
            amount + feeOverEstimate
        //let unspentCoins = this.ListUnspent()
        let rec collectCoins (total: Money) (acc: list<Coin>) (consume: list<Coin>): Money * list<Coin> * list<Coin> =
            if total > amountWithFeeOverEstimate then
                total, acc, consume
            else
                match List.tryHead consume with
                | Some coin ->
                    collectCoins (total + coin.Amount) (List.append acc [coin]) (List.tail consume)
                | None -> failwith "not enough funds to send fat tx"
        let total, coins, remainingUnspent = collectCoins Money.Zero List.empty unspentCoins
        (*
        let valueUnspent =
            List.fold
                (fun totalUnspent, unspentCoin -> totalUnspent + unspentCoin.Amount)
                Money.Zero
                remainingUnspent
        *)

        let rec makeFatTx (minFeeOpt: Option<Money>) (maxFeeOpt: Option<Money>) (attemptedFee: Money): Transaction * list<Money> =
            let transaction = Network.RegTest.CreateTransaction()
            for coin in coins do
                let txIn = TxIn coin.Outpoint
                transaction.Inputs.Add txIn |> ignore
            let txOut =
                let size = (int32 weight) / 4
                let scriptPubKey =
                    seq {
                        for _ in 2 .. size do
                            yield Op.op_Implicit OpcodeType.OP_NOP
                        yield Op.GetPushOp 0L
                    }
                    |> List.ofSeq
                    |> Script
                TxOut(amount, scriptPubKey)
            transaction.Outputs.Add txOut |> ignore
            let changeAmounts =
                if remainingUnspent.Length < 100 then
                    let changeAmount = (total - (amount + attemptedFee)) / 2L
                    let changeTxOut0 =
                        let changeAddress = this.GetNewAddress()
                        TxOut(changeAmount, changeAddress)
                    let changeTxOut1 =
                        let changeAddress = this.GetNewAddress()
                        TxOut(changeAmount, changeAddress)
                    transaction.Outputs.Add changeTxOut0 |> ignore
                    transaction.Outputs.Add changeTxOut1 |> ignore
                    [changeAmount; changeAmount]
                else
                    let changeAmount = total - (amount + attemptedFee)
                    let changeTxOut =
                        let changeAddress = this.GetNewAddress()
                        TxOut(changeAmount, changeAddress)
                    transaction.Outputs.Add changeTxOut |> ignore
                    [changeAmount]
            let signedTransaction = this.SignRawTransactionWithWallet transaction
            let actualWeight = (int64 <| signedTransaction.GetVirtualSize()) * 4L
            let totalOut =
                Seq.fold
                    (fun (total: Money) (output: TxOut) -> total + output.Value)
                    Money.Zero
                    signedTransaction.Outputs
            assert
                let totalIn =
                    Seq.fold
                        (fun (total: Money) (input: TxIn) -> 
                            let coin =
                                Seq.exactlyOne <| Seq.filter (fun (coin: Coin) -> coin.Outpoint = input.PrevOut) unspentCoins
                            total + coin.Amount
                        )
                        Money.Zero
                        signedTransaction.Inputs
                totalIn = total
            let actualFee = total - totalOut
            let actualFeeRate = FeeRatePerKw <| uint32 (actualFee.Satoshi * 1000L / actualWeight)
            if actualFeeRate > feeRate then
                let nextMaxFee =
                    match maxFeeOpt with
                    | Some maxFee -> min maxFee attemptedFee
                    | None -> attemptedFee + Money(1m, MoneyUnit.Satoshi)
                let adjustedFee = Money((attemptedFee.ToDecimal MoneyUnit.Satoshi) * (decimal feeRate.Value) / (decimal actualFeeRate.Value), MoneyUnit.Satoshi)
                let nextAttemptedFee =
                    match minFeeOpt with
                    | Some minFee -> max minFee adjustedFee
                    | None -> adjustedFee
                if nextAttemptedFee < nextMaxFee then
                    makeFatTx minFeeOpt (Some nextMaxFee) nextAttemptedFee
                else
                    (signedTransaction, changeAmounts)
            elif actualFeeRate < feeRate then
                let nextMinFee =
                    match minFeeOpt with
                    | Some minFee -> max minFee attemptedFee
                    | None -> attemptedFee + Money(1m, MoneyUnit.Satoshi)
                let adjustedFee = Money((attemptedFee.ToDecimal MoneyUnit.Satoshi) * (decimal feeRate.Value) / (decimal actualFeeRate.Value), MoneyUnit.Satoshi)
                let nextAttemptedFee =
                    match maxFeeOpt with
                    | Some maxFee -> min maxFee adjustedFee
                    | None -> adjustedFee
                if nextAttemptedFee > nextMinFee then
                    makeFatTx (Some nextMinFee) maxFeeOpt nextAttemptedFee
                else
                    (signedTransaction, changeAmounts)
            else
                (signedTransaction, changeAmounts)

        let signedTransaction, changeAmounts = makeFatTx None None (feeRate.CalculateFeeFromWeight (uint64 weight))



        // TODO: remove these checks

        let actualWeight = (int64 <| signedTransaction.GetVirtualSize()) * 4L
        let totalOut =
            Seq.fold
                (fun (total: Money) (output: TxOut) -> total + output.Value)
                Money.Zero
                signedTransaction.Outputs
        let totalIn =
            Seq.fold
                (fun (total: Money) (input: TxIn) -> 
                    let coin =
                        Seq.exactlyOne <| Seq.filter (fun (coin: Coin) -> coin.Outpoint = input.PrevOut) unspentCoins
                    total + coin.Amount
                )
                Money.Zero
                signedTransaction.Inputs
        let actualFee = totalIn - totalOut
        let actualFeeRate = actualFee.Satoshi * 1000L / actualWeight
        Console.WriteLine(sprintf "ZORK: actualWeight == %A; weight == %A" actualWeight weight)
        Console.WriteLine(sprintf "ZORK: totalOut == %A; totalIn == %A; actualFee == %A" totalOut totalIn actualFee)
        Console.WriteLine(sprintf "ZORK: actualFeeRate == %A; feeRate == %A" actualFeeRate feeRate.Value)
        if (float actualFeeRate) < (float feeRate.Value) * 0.95 || (float actualFeeRate) > (float feeRate.Value) * 1.05 then
            failwithf "oh dear"

        //Console.WriteLine(sprintf "sending fat tx of size %i" size)
        //this.TrySendRawTransaction signedTransaction
        let txIdOpt = this.TrySendRawTransaction signedTransaction
        match txIdOpt with
        | None -> None
        | Some txId ->
            let newCoins, _finalTxOutIndex =
                List.mapFold
                    (fun txOutIndex changeAmount ->
                        (Coin(txId.Value, txOutIndex, changeAmount, Script.Empty), txOutIndex + 1u)
                    )
                    1u
                    changeAmounts
            Some <| List.append remainingUnspent newCoins
        
    member this.FillCurrentBlock (feeRate: FeeRatePerKw): unit = 
        let maxFatTxWeight = 40000u
        let maxMempoolWeight = 4000000u
        let mempoolBurnTxWeight = maxMempoolWeight / 100u
        let mempoolFatTxWeight = maxMempoolWeight + mempoolBurnTxWeight

        let initialMempoolWeight = this.GetMempoolInfo().TotalWeight
        Console.WriteLine(sprintf "BLAH: initialMempoolWeight == %A" initialMempoolWeight)

        let burnAddress =
            let key = new Key()
            let pubKey = key.PubKey
            pubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
        let rec fillBlockWithBurnTxs (prevMempoolWeightOpt: Option<uint32>) =
            Console.WriteLine(sprintf "prevMempoolWeightOpt == %A" prevMempoolWeightOpt)
            let mempoolWeight = this.GetMempoolInfo().TotalWeight
            match prevMempoolWeightOpt with
            | Some prevMempoolWeight ->
                assert (prevMempoolWeight < mempoolWeight)
            | None -> ()
            if mempoolWeight < mempoolBurnTxWeight then
                let adjustedFeeRate = 
                    let adjustment = 0.9 + 0.2 * (new Random()).NextDouble()
                    FeeRatePerKw <| uint32 ((double feeRate.Value) * adjustment)
                this.SetFeeRateForBitcoindTransactions adjustedFeeRate
                let _txId = this.SendToAddress burnAddress (Money(0.0001m, MoneyUnit.BTC))
                fillBlockWithBurnTxs (Some mempoolWeight)

        let rec fillBlockWithFatTxs (unspentCoins: list<Coin>) =
            Console.WriteLine(sprintf "getting mempool info")
            let mempoolInfo = this.GetMempoolInfo()
            Console.WriteLine(sprintf "mempoolinfo == %A" mempoolInfo)
            let mempoolWeight = mempoolInfo.TotalWeight
            if mempoolWeight < mempoolFatTxWeight then
                let weight = min maxFatTxWeight (mempoolFatTxWeight - mempoolWeight)
                //Console.WriteLine(sprintf "FOO - %i unspent coins, mempoolWeight == %i, weight == %i" unspentCoins.Length mempoolWeight weight)
                let newUnspentCoinsOpt = this.TrySendFatTx unspentCoins (Money(0.01m, MoneyUnit.BTC)) weight feeRate
                match newUnspentCoinsOpt with
                | Some newUnspentCoins ->
                    fillBlockWithFatTxs newUnspentCoins
                | None -> failwith "failed to sent fat tx"

        Console.WriteLine(sprintf "listing unspent coins")
        fillBlockWithBurnTxs None
        let unspentCoins = this.ListUnspent()
        //fillBlockWithFatTxs initialTxCount 1000000u
        fillBlockWithFatTxs unspentCoins
        //let newMempoolWeight = this.GetMempoolInfo().TotalWeight
        //fillBlockWithBurnTxs newMempoolWeight
        //let finalMempoolWeight = this.GetMempoolInfo().TotalWeight
        //Console.WriteLine(sprintf "initialMempoolWeight == %i; newMempoolWeight == %i; finalMempoolWeight == %i" initialMempoolWeight newMempoolWeight finalMempoolWeight)

        (*
        let burnAddress =
            let key = new Key()
            let pubKey = key.PubKey
            pubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
        let rec fillBlock (txCount: int) =
            let _txId = this.SendToAddress burnAddress (Money(0.0001m, MoneyUnit.BTC))
            let newTxCount = this.GetTxIdsInMempool().Length
            if newTxCount > txCount then
                fillBlock newTxCount
        let initialTxCount = this.GetTxIdsInMempool().Length
        fillBlock initialTxCount
        *)

    member this.GenerateBlocksRaw (number: BlockHeightOffset32) (address: BitcoinAddress) =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF3 "-regtest -datadir=%s generatetoaddress %i %s" this.DataDir number.Value (address.ToString()))
                Map.empty
                false
        bitcoinCli.WaitForExit()

    member this.GenerateBlocksWithFeeRate (number: BlockHeightOffset32) (address: BitcoinAddress) (feeRate: FeeRatePerKw) =
        Console.WriteLine(sprintf "WOWZERS - generating %i blocks with fee rate %A" number.Value feeRate)
        let rec generateFullBlocks (blockNumber: uint32) =
            if blockNumber < number.Value then
                Console.WriteLine(sprintf "Filling current block")
                this.FillCurrentBlock feeRate
                Console.WriteLine(sprintf "generating raw blocks")
                this.GenerateBlocksRaw (BlockHeightOffset32 1u) address
                //let blockHeight = this.GetBlockHeight()
                //let blockHash = this.GetBlockHash blockHeight
                //let block = this.GetBlock blockHash
                generateFullBlocks (blockNumber + 1u)
        generateFullBlocks 0u

    member this.GenerateBlocks (number: BlockHeightOffset32) (address: BitcoinAddress) =
        let feeRateOpt = this.EstimateSmartFee (BlockHeightOffset16 1us)
        let feeRate = UnwrapOption feeRateOpt "GenerateBlocks called before fee rate has been set"
        this.GenerateBlocksWithFeeRate number address feeRate

    member this.GenerateBlocksToSelf (number: BlockHeightOffset32) =
        let address = this.GetNewAddress()
        this.GenerateBlocks number address

    member this.GetTxIdsInMempool(): list<TxId> =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getrawmempool" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let txIdList = JsonConvert.DeserializeObject<list<string>> output
        List.map (fun (txIdString: string) -> TxId <| uint256 txIdString) txIdList

    member this.EstimateSmartFee (confirmationTarget: BlockHeightOffset16): Option<FeeRatePerKw> =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF2 "-regtest -datadir=%s estimatesmartfee %i" this.DataDir confirmationTarget.Value)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let estimateSmartFeeResponse = JsonConvert.DeserializeObject<EstimateSmartFeeResponse> output

        if Object.ReferenceEquals(null, estimateSmartFeeResponse.errors) || estimateSmartFeeResponse.errors.Length = 0 then
            let btcPerKB = estimateSmartFeeResponse.feerate
            let satPerKB = (Money (btcPerKB, MoneyUnit.BTC)).ToUnit MoneyUnit.Satoshi
            // 4 weight units per byte. See segwit specs.
            let kwPerKB = 4m
            let satPerKw = satPerKB / kwPerKB
            let feeRatePerKw = FeeRatePerKw (uint32 satPerKw)
            Some feeRatePerKw
        else
            None

    member this.GetBalance(): Money =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getbalance" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let output = String.concat "\n" lines
        let balance = JsonConvert.DeserializeObject<decimal> output
        Money(balance, MoneyUnit.BTC)

    member this.GetNewAddress(): BitcoinAddress =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getnewaddress" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let address = (String.concat "\n" lines).Trim()
        BitcoinAddress.Create(address, Network.RegTest)

    (*
    member this.GetRawChangeAddress(): BitcoinAddress =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF1 "-regtest -datadir=%s getrawchangeaddress" this.DataDir)
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let address = (String.concat "\n" lines).Trim()
        BitcoinAddress.Create(address, Network.RegTest)
    *)

    member this.FundByMining (amount: Money): unit =
        let address = this.GetNewAddress()
        let rec fund() =
            let balance = this.GetBalance()
            if balance < amount then
                this.GenerateBlocks (BlockHeightOffset32 (uint32 1)) address
                fund()
        fund()

    member this.SendToAddress (address: BitcoinAddress) (amount: Money): TxId =
        use bitcoinCli =
            ProcessWrapper.New
                "bitcoin-cli"
                this.WorkDir
                (SPrintF3 "-regtest -datadir=%s sendtoaddress %s %M" this.DataDir (address.ToString()) (amount.ToUnit MoneyUnit.BTC))
                Map.empty
                false
        let lines = bitcoinCli.ReadToEnd()
        let txIdString = (String.concat "\n" lines).Trim()
        let wowzers =
            try
                uint256 txIdString
            with
            | _ ->
                failwithf "got this from sendtoaddress == %s" txIdString
        TxId <| wowzers

    member this.RpcUrl: string =
        SPrintF2 "http://%s:%s@127.0.0.1:18554" this.RpcUser this.RpcPassword

type ElectrumServer = {
    DbDir: string
    ProcessWrapper: ProcessWrapper
} with
    interface IDisposable with
        member this.Dispose() =
            this.ProcessWrapper.Process.Kill()
            this.ProcessWrapper.WaitForExit()
            (this.ProcessWrapper :> IDisposable).Dispose()
            Directory.Delete(this.DbDir, true)

    static member Start(bitcoind: Bitcoind): ElectrumServer =
        let dbDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory dbDir |> ignore
        let processWrapper =
            ProcessWrapper.New
                "electrumx_server"
                bitcoind.WorkDir
                ""
                (Map.ofList <| [
                    "SERVICES", "tcp://[::1]:50001";
                    "COIN", "BitcoinSegwit";
                    "NET", "regtest";
                    "DAEMON_URL", bitcoind.RpcUrl;
                    "DB_DIRECTORY", dbDir
                ])
                true
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "TCP server listening on [::1]:50001")
        {
            DbDir = dbDir
            ProcessWrapper = processWrapper
        }

    static member EstimateFeeRate(): Async<FeeRatePerKw> = async {
        let! btcPerKB =
            let averageFee (feesFromDifferentServers: list<decimal>): decimal =
                feesFromDifferentServers.Sum() / decimal (List.length feesFromDifferentServers)
            let estimateFeeJob = ElectrumClient.EstimateFee 6
            Server.Query
                Currency.BTC
                (QuerySettings.FeeEstimation averageFee)
                estimateFeeJob
                None
        let satPerKB = (Money (btcPerKB, MoneyUnit.BTC)).ToUnit MoneyUnit.Satoshi
        // 4 weight units per byte. See segwit specs.
        let kwPerKB = 4m
        let satPerKw = satPerKB / kwPerKB
        let feeRatePerKw = FeeRatePerKw (uint32 satPerKw)
        return feeRatePerKw
    }

    (*
    static member SetEstimatedFeeRate(feeRatePerKw: FeeRatePerKw) =
        let satPerKw = decimal feeRatePerKw.Value
        let kwPerKB = 4m
        let satPerKB = satPerKw * kwPerKB
        let btcPerKB = (Money (satPerKB, MoneyUnit.Satoshi)).ToUnit MoneyUnit.BTC
        ElectrumClient.SetRegTestFakeFeeRate btcPerKB
    *)

type Lnd = {
    LndDir: string
    ProcessWrapper: ProcessWrapper
    ConnectionString: string
    ClientFactory: ILightningClientFactory
} with
    interface IDisposable with
        member this.Dispose() =
            this.ProcessWrapper.Process.Kill()
            this.ProcessWrapper.WaitForExit()
            (this.ProcessWrapper :> IDisposable).Dispose()
            Directory.Delete(this.LndDir, true)

    static member Start(bitcoind: Bitcoind): Async<Lnd> = async {
        let lndDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory lndDir |> ignore
        let processWrapper =
            let args =
                ""
                + " --bitcoin.active"
                + " --bitcoin.regtest"
                + " --bitcoin.node=bitcoind"
                + " --bitcoind.rpcuser=" + bitcoind.RpcUser
                + " --bitcoind.rpcpass=" + bitcoind.RpcPassword
                + " --bitcoind.zmqpubrawblock=tcp://127.0.0.1:28332"
                + " --bitcoind.zmqpubrawtx=tcp://127.0.0.1:28333"
                + " --bitcoind.rpchost=localhost:18554"
                + " --debuglevel=trace"
                + " --listen=127.0.0.2"
                + " --restlisten=127.0.0.2:8080"
                + " --lnddir=" + lndDir
            ProcessWrapper.New
                "lnd"
                bitcoind.WorkDir
                args
                Map.empty
                false
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "password gRPC proxy started at 127.0.0.2:8080")
        let connectionString = 
            ""
            + "type=lnd-rest;"
            + "server=https://127.0.0.2:8080;"
            + "allowinsecure=true;"
            + "macaroonfilepath=" + Path.Combine(lndDir, "data/chain/bitcoin/regtest/admin.macaroon")
        let clientFactory = new LightningClientFactory(NBitcoin.Network.RegTest) :> ILightningClientFactory
        let lndClient = clientFactory.Create connectionString :?> LndClient
        let walletPassword = Path.GetRandomFileName()
        let! genSeedResp = Async.AwaitTask <| lndClient.SwaggerClient.GenSeedAsync(null, null)
        let initWalletReq =
            LnrpcInitWalletRequest (
                Wallet_password = Encoding.ASCII.GetBytes walletPassword,
                Cipher_seed_mnemonic = genSeedResp.Cipher_seed_mnemonic
            )

        let! _ = Async.AwaitTask <| lndClient.SwaggerClient.InitWalletAsync initWalletReq
        processWrapper.WaitForMessage (fun msg -> msg.EndsWith "Server listening on 127.0.0.2:9735")
        return {
            LndDir = lndDir
            ProcessWrapper = processWrapper
            ConnectionString = connectionString
            ClientFactory = clientFactory
        }
    }

    member this.Client(): LndClient =
        this.ClientFactory.Create this.ConnectionString :?> LndClient

    member this.GetEndPoint(): Async<NodeEndPoint> = async {
        let client = this.Client()
        let! getInfo = Async.AwaitTask (client.SwaggerClient.GetInfoAsync())
        return NodeEndPoint.Parse Currency.BTC (SPrintF1 "%s@127.0.0.2:9735" getInfo.Identity_pubkey)
    }

    member this.GetDepositAddress(): Async<BitcoinAddress> =
        let client = this.Client()
        (client :> ILightningClient).GetDepositAddress ()
        |> Async.AwaitTask

    member this.GetBlockHeight(): Async<BlockHeight> = async {
        let client = this.Client()
        use task = client.SwaggerClient.GetInfoAsync()
        let! getInfo = Async.AwaitTask task
        return BlockHeight (uint32 getInfo.Block_height.Value)
    }

    member this.WaitForBlockHeight(blockHeight: BlockHeight): Async<unit> = async {
        let! currentBlockHeight = this.GetBlockHeight()
        if blockHeight > currentBlockHeight then
            this.ProcessWrapper.WaitForMessage <| fun msg ->
                msg.Contains(SPrintF1 "New block: height=%i" blockHeight.Value)
        return ()
    }

    member this.Balance(): Async<Money> = async {
        let client = this.Client()
        use task = client.SwaggerClient.WalletBalanceAsync ()
        let! balance = Async.AwaitTask task
        return Money(uint64 balance.Confirmed_balance, MoneyUnit.Satoshi)
    }

    member this.WaitForBalance(money: Money): Async<unit> = async {
        let! currentBalance = this.Balance()
        if money > currentBalance then
            this.ProcessWrapper.WaitForMessage <| fun msg ->
                msg.Contains "[walletbalance]"
            return! this.WaitForBalance money
        return ()
    }
    
    member this.SendCoins(money: Money) (address: BitcoinAddress) (feerate: FeeRatePerKw): Async<TxId> = async {
        let client = this.Client()
        let sendCoinsReq =
            LnrpcSendCoinsRequest (
                Addr = address.ToString(),
                Amount = (money.ToUnit MoneyUnit.Satoshi).ToString(),
                Sat_per_byte = feerate.Value.ToString()
            )
        use task = client.SwaggerClient.SendCoinsAsync sendCoinsReq
        let! sendCoinsResp = Async.AwaitTask task
        return TxId <| uint256 sendCoinsResp.Txid
    }

    member this.ConnectTo (nodeEndPoint: NodeEndPoint) : Async<ConnectionResult> =
        let client = this.Client()
        let nodeInfo =
            let pubKey =
                let stringified = nodeEndPoint.NodeId.ToString()
                let unstringified = PubKey stringified
                unstringified
            NodeInfo (pubKey, nodeEndPoint.IPEndPoint.Address.ToString(), nodeEndPoint.IPEndPoint.Port)
        (Async.AwaitTask: Task<ConnectionResult> -> Async<ConnectionResult>) <| (client :> ILightningClient).ConnectTo nodeInfo

    member this.OpenChannel (nodeEndPoint: NodeEndPoint)
                            (amount: Money)
                            (feeRate: FeeRatePerKw)
                                : Async<Result<unit, OpenChannelResult>> = async {
        let client = this.Client()
        let nodeInfo =
            let pubKey =
                let stringified = nodeEndPoint.NodeId.ToString()
                let unstringified = PubKey stringified
                unstringified
            NodeInfo (pubKey, nodeEndPoint.IPEndPoint.Address.ToString(), nodeEndPoint.IPEndPoint.Port)
        let openChannelReq =
            new OpenChannelRequest (
                NodeInfo = nodeInfo,
                ChannelAmount = amount,
                FeeRate = new FeeRate(Money(uint64 feeRate.Value))
            )
        let! openChannelResponse = Async.AwaitTask <| (client :> ILightningClient).OpenChannel openChannelReq
        match openChannelResponse.Result with
        | OpenChannelResult.Ok -> return Ok ()
        | err -> return Error err
    }

    member this.CloseChannel (fundingOutPoint: OutPoint)
                                 : Async<unit> = async {
        let client = this.Client()
        let fundingTxIdStr = fundingOutPoint.Hash.ToString()
        let fundingOutputIndex = fundingOutPoint.N
        try
            let! _response =
                Async.AwaitTask
                <| client.SwaggerClient.CloseChannelAsync(fundingTxIdStr, int64 fundingOutputIndex)
            return ()
        with
        | ex ->
            // BTCPayServer.Lightning is broken and doesn't handle the
            // channel-closed reply from lnd properly. This catches the exception (and
            // hopefully not other, unrelated exceptions).
            // See: https://github.com/btcpayserver/BTCPayServer.Lightning/issues/38
            match FSharpUtil.FindException<Newtonsoft.Json.JsonReaderException> ex with
            | Some ex when ex.LineNumber = 2 && ex.LinePosition = 0 -> return ()
            | _ -> return raise <| FSharpUtil.ReRaise ex
    }

type WalletInstance private (password: string, channelStore: ChannelStore, node: Node) =
    static let oneWalletAtATime: Semaphore = new Semaphore(1, 1)

    static member New (listenEndpointOpt: Option<IPEndPoint>) (privateKeyOpt: Option<Key>) = async {
        oneWalletAtATime.WaitOne() |> ignore
        let password = Path.GetRandomFileName()
        let privateKeyBytes =
            let privateKey =
                match privateKeyOpt with
                | Some privateKey -> privateKey
                | None -> new Key()
            let privateKeyBytesLength = 32
            let bytes: array<byte> = Array.zeroCreate privateKeyBytesLength
            use bytesStream = new MemoryStream(bytes)
            let stream = NBitcoin.BitcoinStream(bytesStream, true)
            privateKey.ReadWrite stream
            bytes

        do! Account.CreateAllAccounts privateKeyBytes password
        let btcAccount =
            let account = Account.GetAllActiveAccounts() |> Seq.filter (fun x -> x.Currency = Currency.BTC) |> Seq.head
            account :?> NormalUtxoAccount
        let channelStore = ChannelStore btcAccount
        let node =
            let listenEndpoint =
                match listenEndpointOpt with
                | Some listenEndpoint -> listenEndpoint
                | None -> IPEndPoint(IPAddress.Parse "127.0.0.1", 0)
            Connection.Start channelStore password listenEndpoint
        return new WalletInstance(password, channelStore, node)
    }

    interface IDisposable with
        member this.Dispose() =
            Account.WipeAll()
            oneWalletAtATime.Release() |> ignore

    member self.Account: IAccount =
        Account.GetAllActiveAccounts() |> Seq.filter (fun x -> x.Currency = Currency.BTC) |> Seq.head

    member self.Address: BitcoinScriptAddress =
        BitcoinScriptAddress(self.Account.PublicAddress, Network.RegTest)

    member self.Password: string = password
    member self.ChannelStore: ChannelStore = channelStore
    member self.Node: Node = node
    member self.NodeEndPoint = Lightning.Network.EndPoint self.Node

    member self.GetBalance(): Async<Money> = async {
        let btcAccount = self.Account :?> NormalUtxoAccount
        let! cachedBalance = Account.GetShowableBalance btcAccount ServerSelectionMode.Analysis None
        match cachedBalance with
        | NotFresh _ ->
            do! Async.Sleep 500
            return! self.GetBalance()
        | Fresh amount -> return Money(amount, MoneyUnit.BTC)
    }

    member self.WaitForBalance(minAmount: Money): Async<Money> = async {
        let btcAccount = self.Account :?> NormalUtxoAccount
        let! cachedBalance = Account.GetShowableBalance btcAccount ServerSelectionMode.Analysis None
        match cachedBalance with
        | Fresh amount when amount < minAmount.ToDecimal MoneyUnit.BTC ->
            do! Async.Sleep 500
            return! self.WaitForBalance minAmount
        | NotFresh _ ->
            do! Async.Sleep 500
            return! self.WaitForBalance minAmount
        | Fresh amount -> return Money(amount, MoneyUnit.BTC)
    }

    member self.WaitForFundingConfirmed (channelId: ChannelIdentifier): Async<unit> =
        let channelInfo = self.ChannelStore.ChannelInfo channelId
        let fundingBroadcastButNotLockedData =
            match channelInfo.Status with
            | ChannelStatus.FundingBroadcastButNotLocked fundingBroadcastButNotLockedData
                -> fundingBroadcastButNotLockedData
            | status -> failwith (SPrintF1 "Unexpected channel status. Expected FundingBroadcastButNotLocked, got %A" status)
        let rec waitForFundingConfirmed() = async {
            let! remainingConfirmations = fundingBroadcastButNotLockedData.GetRemainingConfirmations()
            if remainingConfirmations > 0u then
                do! Async.Sleep 1000
                return! waitForFundingConfirmed()
            else
                // TODO: the backend API doesn't give us any way to avoid
                // the FundingOnChainLocationUnknown error, so just sleep
                // to avoid the race condition. This waiting should really
                // be implemented on the backend anyway.
                do! Async.Sleep 10000
                return ()
        }
        waitForFundingConfirmed()

[<TestFixture>]
type LN() =
    do Config.SetRunModeTesting()

    let FundeeAccountsPrivateKey =
        // Note: The key needs to be hard-coded, as opposed to randomly
        // generated, since it is used in two separate processes and must be
        // the same in each process.
        new Key(uint256.Parse("9d1ee30acb68716ed5f4e25b3c052c6078f1813f45d33a47e46615bfd05fa6fe").ToBytes())
    let FundeeNodePubKey =
        Connection.NodeIdAsPubKeyFromAccountPrivKey FundeeAccountsPrivateKey
    let FundeeLightningIPEndpoint = IPEndPoint (IPAddress.Parse "127.0.0.1", 9735)
    let FundeeNodeEndpoint =
        NodeEndPoint.Parse
            Currency.BTC
            (SPrintF3
                "%s@%s:%d"
                (FundeeNodePubKey.ToHex())
                (FundeeLightningIPEndpoint.Address.ToString())
                FundeeLightningIPEndpoint.Port
            )

    let WalletToWalletTestPayment0Amount = Money(0.01m, MoneyUnit.BTC)
    let WalletToWalletTestPayment1Amount = Money(0.015m, MoneyUnit.BTC)

    [<Category("GeewalletToGeewalletFunder")>]
    [<Test>]
    [<Timeout(1800000)>]
    member __.``can send/receive monohop payments and close channel (funder)``() = Async.RunSynchronously <| async {
        use! walletInstance = WalletInstance.New None None
        use bitcoind = Bitcoind.Start()
        use _electrumServer = ElectrumServer.Start bitcoind
        //use! lnd = Lnd.Start bitcoind
        let feeRate = FeeRatePerKw 5000u

        Console.WriteLine(sprintf "FUNDER - QQQ")


        (*
        // As explained in the other test, geewallet cannot use coinbase outputs.
        // To work around that we mine a block to a LND instance and afterwards tell
        // it to send funds to the funder geewallet instance
        let! address = lnd.GetDepositAddress()
        let blocksMinedToLnd = BlockHeightOffset32 1u
        bitcoind.GenerateBlocks blocksMinedToLnd address
        Console.WriteLine(sprintf "FUNDER - EEE")

        let maturityDurationInNumberOfBlocks = BlockHeightOffset32 (uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity)
        bitcoind.GenerateBlocksToSelf maturityDurationInNumberOfBlocks
        Console.WriteLine(sprintf "FUNDER - RRR")

        // We confirm the one block mined to LND, by waiting for LND to see the chain
        // at a height which has that block matured. The height at which the block will
        // be matured is 100 on regtest. Since we initialally mined one block for LND,
        // this will wait until the block height of LND reaches 1 (initial blocks mined)
        // plus 100 blocks (coinbase maturity). This test has been parameterized
        // to use the constants defined in NBitcoin, but you have to keep in mind that
        // the coinbase maturity may be defined differently in other coins.
        do! lnd.WaitForBlockHeight (BlockHeight.Zero + blocksMinedToLnd + maturityDurationInNumberOfBlocks)
        do! lnd.WaitForBalance (Money(50UL, MoneyUnit.BTC))
        Console.WriteLine(sprintf "FUNDER - TTT")
        *)

        // fund geewallet
        let geewalletAccountAmount = Money (25m, MoneyUnit.BTC)
        let _txId = bitcoind.SendToAddress walletInstance.Address geewalletAccountAmount
        //let! feeRate = ElectrumServer.EstimateFeeRate()
        //let! _txid = lnd.SendCoins geewalletAccountAmount walletInstance.Address feeRate
        Console.WriteLine(sprintf "FUNDER - YYY")

        (*
        // wait for lnd's transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            Thread.Sleep 500
        Console.WriteLine(sprintf "FUNDER - UUU")
        *)

        // We want to make sure Geewallet consideres the money received.
        // A typical number of blocks that is almost universally considered
        // 100% confirmed, is 6. Therefore we mine 7 blocks. Because we have
        // waited for the transaction to appear in bitcoind's mempool, we
        // can assume that the first of the 7 blocks will include the
        // transaction sending money to Geewallet. The next 6 blocks will
        // bury the first block, so that the block containing the transaction
        // will be 6 deep at the end of the following call to generateBlocks.
        // At that point, the 0.25 regtest coins from the above call to sendcoins
        // are considered arrived to Geewallet.
        let consideredConfirmedAmountOfBlocksPlusOne = BlockHeightOffset32 7u
        bitcoind.GenerateBlocksToSelf consideredConfirmedAmountOfBlocksPlusOne
        Console.WriteLine(sprintf "FUNDER - III")

        let fundingAmount = Money(0.1m, MoneyUnit.BTC)
        let! transferAmount = async {
            let! accountBalance = walletInstance.WaitForBalance fundingAmount
            return TransferAmount (fundingAmount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
        }
        Console.WriteLine(sprintf "FUNDER - OOO")
        let wowzers = bitcoind.EstimateSmartFee (BlockHeightOffset16 1us)
        Console.WriteLine(sprintf "FUNDER - fee is currently %A" wowzers)
        let! metadata = ChannelManager.EstimateChannelOpeningFee (walletInstance.Account :?> NormalUtxoAccount) transferAmount
        Console.WriteLine(sprintf "FUNDER - PPP")
        let! pendingChannelRes =
            Lightning.Network.OpenChannel
                walletInstance.Node
                FundeeNodeEndpoint
                transferAmount
                metadata
                walletInstance.Password
        Console.WriteLine(sprintf "FUNDER - AAA")
        let pendingChannel = UnwrapResult pendingChannelRes "OpenChannel failed"
        let minimumDepth = (pendingChannel :> IChannelToBeOpened).ConfirmationsRequired
        let channelId = (pendingChannel :> IChannelToBeOpened).ChannelId
        let! fundingTxIdRes = pendingChannel.Accept()
        Console.WriteLine(sprintf "FUNDER - SSS")
        let _fundingTxId = UnwrapResult fundingTxIdRes "pendingChannel.Accept failed"
        Console.WriteLine(sprintf "FUNDER - DDD")
        bitcoind.GenerateBlocksToSelf (BlockHeightOffset32 minimumDepth)
        Console.WriteLine(sprintf "FUNDER - FFF")

        do!
            let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
            let fundingBroadcastButNotLockedData =
                match channelInfo.Status with
                | ChannelStatus.FundingBroadcastButNotLocked fundingBroadcastButNotLockedData
                    -> fundingBroadcastButNotLockedData
                | status -> failwith (SPrintF1 "Unexpected channel status. Expected FundingBroadcastButNotLocked, got %A" status)
            let rec waitForFundingConfirmed() = async {
                let! remainingConfirmations = fundingBroadcastButNotLockedData.GetRemainingConfirmations()
                if remainingConfirmations > 0u then
                    do! Async.Sleep 1000
                    return! waitForFundingConfirmed()
                else
                    // TODO: the backend API doesn't give us any way to avoid
                    // the FundingOnChainLocationUnknown error, so just sleep
                    // to avoid the race condition. This waiting should really
                    // be implemented on the backend anyway.
                    do! Async.Sleep 10000
                    return ()
            }
            waitForFundingConfirmed()
        Console.WriteLine(sprintf "FUNDER - GGG")

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfo.Balance, MoneyUnit.BTC) <> fundingAmount then
            failwith "balance does not match funding amount"

        let! sendMonoHopPayment0Res =
            let transferAmount =
                let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                TransferAmount (WalletToWalletTestPayment0Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
            Lightning.Network.SendMonoHopPayment
                walletInstance.Node
                channelId
                transferAmount
        UnwrapResult sendMonoHopPayment0Res "SendMonoHopPayment failed"

        let channelInfoAfterPayment0 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment0.Balance, MoneyUnit.BTC) <> fundingAmount - WalletToWalletTestPayment0Amount then
            failwith "incorrect balance after payment 0"

        let! sendMonoHopPayment1Res =
            let transferAmount =
                let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                TransferAmount (WalletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
            Lightning.Network.SendMonoHopPayment
                walletInstance.Node
                channelId
                transferAmount
        UnwrapResult sendMonoHopPayment1Res "SendMonoHopPayment failed"

        let channelInfoAfterPayment1 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> fundingAmount - WalletToWalletTestPayment0Amount - WalletToWalletTestPayment1Amount then
            failwith "incorrect balance after payment 1"

        //do! lnd.FloodTransactionsToSetFeeRate bitcoind feeRate

        //ElectrumServer.SetEstimatedFeeRate (FeeRatePerKw (feeRate.Value * 4u))
        bitcoind.SetFeeRateByMining (feeRate * 4u)
        let! newFeeRateOpt = walletInstance.ChannelStore.FeeUpdateRequired channelId
        let newFeeRate = UnwrapOption newFeeRateOpt "Fee update should be required"
        Console.WriteLine(sprintf "new fee is %A" newFeeRate)
        let! updateFeeRes =
            Lightning.Network.UpdateFee walletInstance.Node channelId newFeeRate
        UnwrapResult updateFeeRes "UpdateFee failed"

        //ElectrumServer.SetEstimatedFeeRate (FeeRatePerKw (uint32 0))

        let! closeChannelRes = Lightning.Network.CloseChannel walletInstance.Node channelId
        match closeChannelRes with
        | Ok _ -> ()
        | Error err -> failwith (SPrintF1 "error when closing channel: %s" err.Message)

        match (walletInstance.ChannelStore.ChannelInfo channelId).Status with
        | ChannelStatus.Closing -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Closing, got %A" status)

        // Mine 10 blocks to make sure closing tx is confirmed
        bitcoind.GenerateBlocksToSelf (BlockHeightOffset32 (uint32 10))
        
        let rec waitForClosingTxConfirmed attempt = async {
            Infrastructure.LogDebug (SPrintF1 "Checking if closing tx is finished, attempt #%d" attempt)
            if attempt = 10 then
                return Error "Closing tx not confirmed after maximum attempts"
            else
                let! txIsConfirmed = Lightning.Network.CheckClosingFinished (walletInstance.ChannelStore.ChannelInfo channelId)
                if txIsConfirmed then
                    return Ok ()
                else
                    do! Async.Sleep 1000
                    return! waitForClosingTxConfirmed (attempt + 1)
                    
        }

        let! closingTxConfirmedRes = waitForClosingTxConfirmed 0
        match closingTxConfirmedRes with
        | Ok _ -> ()
        | Error err -> failwith (SPrintF1 "error when waiting for closing tx to confirm: %s" err)

        return ()
    }

    [<Category("GeewalletToGeewalletFundee")>]
    [<Test>]
    [<Timeout(1800000)>]
    member __.``can send/receive monohop payments and close channel (fundee)``() = Async.RunSynchronously <| async {
        Console.WriteLine(sprintf "FUNDEE - QQQ")
        use! walletInstance = WalletInstance.New (Some FundeeLightningIPEndpoint) (Some FundeeAccountsPrivateKey)
        Console.WriteLine(sprintf "FUNDEE - WWW")
        //let! feeRate = ElectrumServer.EstimateFeeRate()
        let! pendingChannelRes =
            Lightning.Network.AcceptChannel
                walletInstance.Node
        Console.WriteLine(sprintf "FUNDEE - EEE")

        let (channelId, _) = UnwrapResult pendingChannelRes "OpenChannel failed"

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        Console.WriteLine(sprintf "FUNDEE - RRR")
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! feeRate = ElectrumServer.EstimateFeeRate()

        if Money(channelInfo.Balance, MoneyUnit.BTC) <> Money(0.0m, MoneyUnit.BTC) then
            failwith "incorrect balance after accepting channel"

        Console.WriteLine(sprintf "FUNDEE - TTT")
        let! receiveMonoHopPaymentRes =
            Lightning.Network.ReceiveMonoHopPayment walletInstance.Node channelId
        Console.WriteLine(sprintf "FUNDEE - YYY")
        UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"

        let channelInfoAfterPayment0 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment0.Balance, MoneyUnit.BTC) <> WalletToWalletTestPayment0Amount then
            failwith "incorrect balance after receiving payment 0"

        let! receiveMonoHopPaymentRes =
            Lightning.Network.ReceiveMonoHopPayment walletInstance.Node channelId
        UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"

        let channelInfoAfterPayment1 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> WalletToWalletTestPayment0Amount + WalletToWalletTestPayment1Amount then
            failwith "incorrect balance after receiving payment 1"

        let newFeeRate = feeRate * 4u
        let rec waitForFeeAdjustment() = async {
            let! currentFeeRate = ElectrumServer.EstimateFeeRate()
            if currentFeeRate < newFeeRate then
                do! Async.Sleep 2000
                return! waitForFeeAdjustment()
            return ()
        }
        do! waitForFeeAdjustment()

        //ElectrumServer.SetEstimatedFeeRate (FeeRatePerKw (feeRate.Value * 4u))
        //bitcoind.SetFeeRate (feeRate * 4u)
        let! acceptUpdateFeeRes =
            Lightning.Network.AcceptUpdateFee walletInstance.Node channelId
        UnwrapResult acceptUpdateFeeRes "AcceptUpdateFee failed"

        let! closeChannelRes = Lightning.Network.AcceptCloseChannel walletInstance.Node channelId
        match closeChannelRes with
        | Ok _ -> ()
        | Error err -> failwith (SPrintF1 "failed to accept close channel: %A" err)

        return ()
    }

    (*
    [<Category("GeewalletToLndFunder")>]
    [<Test>]
    [<Timeout(500000)>]
    member __.``can open and close channel with LND``() = Async.RunSynchronously <| async {
        use! walletInstance = WalletInstance.New None None
        use bitcoind = Bitcoind.Start()
        use _electrumServer = ElectrumServer.Start bitcoind
        use! lnd = Lnd.Start bitcoind

        let! address = lnd.GetDepositAddress()
        let blocksMinedToLnd = BlockHeightOffset32 1u
        bitcoind.GenerateBlocksRaw blocksMinedToLnd address

        // Geewallet cannot use these outputs, even though they are encumbered with an output
        // script from its wallet. This is because they come from coinbase. Coinbase outputs are
        // the source of all bitcoin, and as of May 2020, Geewallet does not detect coins
        // received straight from coinbase. In practice, this doesn't matter, since miners
        // do not use Geewallet. If the coins were to be detected by geewallet,
        // this test would still work. This comment is just here to avoid confusion.
        let maturityDurationInNumberOfBlocks = BlockHeightOffset32 (uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity)
        bitcoind.GenerateBlocksRaw maturityDurationInNumberOfBlocks walletInstance.Address

        // We confirm the one block mined to LND, by waiting for LND to see the chain
        // at a height which has that block matured. The height at which the block will
        // be matured is 100 on regtest. Since we initialally mined one block for LND,
        // this will wait until the block height of LND reaches 1 (initial blocks mined)
        // plus 100 blocks (coinbase maturity). This test has been parameterized
        // to use the constants defined in NBitcoin, but you have to keep in mind that
        // the coinbase maturity may be defined differently in other coins.
        do! lnd.WaitForBlockHeight (BlockHeight.Zero + blocksMinedToLnd + maturityDurationInNumberOfBlocks)
        do! lnd.WaitForBalance (Money(50UL, MoneyUnit.BTC))

        // fund geewallet
        let geewalletAccountAmount = Money (25m, MoneyUnit.BTC)
        let! feeRate = ElectrumServer.EstimateFeeRate()
        let! _txid = lnd.SendCoins geewalletAccountAmount walletInstance.Address feeRate

        // wait for lnd's transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            Thread.Sleep 500

        // We want to make sure Geewallet consideres the money received.
        // A typical number of blocks that is almost universally considered
        // 100% confirmed, is 6. Therefore we mine 7 blocks. Because we have
        // waited for the transaction to appear in bitcoind's mempool, we
        // can assume that the first of the 7 blocks will include the
        // transaction sending money to Geewallet. The next 6 blocks will
        // bury the first block, so that the block containing the transaction
        // will be 6 deep at the end of the following call to generateBlocks.
        // At that point, the 0.25 regtest coins from the above call to sendcoins
        // are considered arrived to Geewallet.
        let consideredConfirmedAmountOfBlocksPlusOne = BlockHeightOffset32 7u
        bitcoind.GenerateBlocksRaw consideredConfirmedAmountOfBlocksPlusOne walletInstance.Address

        let! lndEndPoint = lnd.GetEndPoint()
        let! transferAmount = async {
            let amount = Money(0.002m, MoneyUnit.BTC)
            let! accountBalance = walletInstance.WaitForBalance amount
            return TransferAmount (amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
        }
        let! metadata = ChannelManager.EstimateChannelOpeningFee (walletInstance.Account :?> NormalUtxoAccount) transferAmount
        let! pendingChannelRes =
            Lightning.Network.OpenChannel
                walletInstance.Node
                lndEndPoint
                transferAmount
                metadata
                walletInstance.Password
        let pendingChannel = UnwrapResult pendingChannelRes "OpenChannel failed"
        let minimumDepth = (pendingChannel :> IChannelToBeOpened).ConfirmationsRequired
        let channelId = (pendingChannel :> IChannelToBeOpened).ChannelId
        let! fundingTxIdRes = pendingChannel.Accept()
        let _fundingTxId = UnwrapResult fundingTxIdRes "pendingChannel.Accept failed"
        bitcoind.GenerateBlocksRaw (BlockHeightOffset32 minimumDepth) walletInstance.Address

        do! walletInstance.WaitForFundingConfirmed channelId

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        let! closeChannelRes = Lightning.Network.CloseChannel walletInstance.Node channelId
        match closeChannelRes with
        | Ok _ -> ()
        | Error err -> failwith (SPrintF1 "error when closing channel: %s" err.Message)

        match (walletInstance.ChannelStore.ChannelInfo channelId).Status with
        | ChannelStatus.Closing -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Closing, got %A" status)

        // Mine 10 blocks to make sure closing tx is confirmed
        bitcoind.GenerateBlocksRaw (BlockHeightOffset32 (uint32 10)) walletInstance.Address
        
        let rec waitForClosingTxConfirmed attempt = async {
            Infrastructure.LogDebug (SPrintF1 "Checking if closing tx is finished, attempt #%d" attempt)
            if attempt = 10 then
                return Error "Closing tx not confirmed after maximum attempts"
            else
                let! txIsConfirmed = Lightning.Network.CheckClosingFinished (walletInstance.ChannelStore.ChannelInfo channelId)
                if txIsConfirmed then
                    return Ok ()
                else
                    do! Async.Sleep 1000
                    return! waitForClosingTxConfirmed (attempt + 1)
                    
        }

        let! closingTxConfirmedRes = waitForClosingTxConfirmed 0
        match closingTxConfirmedRes with
        | Ok _ -> ()
        | Error err -> failwith (SPrintF1 "error when waiting for closing tx to confirm: %s" err)

        return ()
    }

    [<Category("GeewalletToLndFundee")>]
    [<Test>]
    [<Timeout(500000)>]
    member __.``can accept and close channel from LND``() = Async.RunSynchronously <| async {
        use! walletInstance = WalletInstance.New None None
        use bitcoind = Bitcoind.Start()
        use _electrumServer = ElectrumServer.Start bitcoind
        use! lnd = Lnd.Start bitcoind

        let! address = lnd.GetDepositAddress()
        let blocksMinedToLnd = BlockHeightOffset32 1u
        bitcoind.GenerateBlocksRaw blocksMinedToLnd address

        // Geewallet cannot use these outputs, even though they are encumbered with an output
        // script from its wallet. This is because they come from coinbase. Coinbase outputs are
        // the source of all bitcoin, and as of May 2020, Geewallet does not detect coins
        // received straight from coinbase. In practice, this doesn't matter, since miners
        // do not use Geewallet. If the coins were to be detected by geewallet,
        // this test would still work. This comment is just here to avoid confusion.
        let maturityDurationInNumberOfBlocks = BlockHeightOffset32 (uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity)
        bitcoind.GenerateBlocksRaw maturityDurationInNumberOfBlocks walletInstance.Address

        // We confirm the one block mined to LND, by waiting for LND to see the chain
        // at a height which has that block matured. The height at which the block will
        // be matured is 100 on regtest. Since we initialally mined one block for LND,
        // this will wait until the block height of LND reaches 1 (initial blocks mined)
        // plus 100 blocks (coinbase maturity). This test has been parameterized
        // to use the constants defined in NBitcoin, but you have to keep in mind that
        // the coinbase maturity may be defined differently in other coins.
        do! lnd.WaitForBlockHeight (BlockHeight.Zero + blocksMinedToLnd + maturityDurationInNumberOfBlocks)
        do! lnd.WaitForBalance (Money(50UL, MoneyUnit.BTC))

        let! feeRate = ElectrumServer.EstimateFeeRate()
        let acceptChannelTask = Lightning.Network.AcceptChannel walletInstance.Node
        let openChannelTask = async {
            let! connectionResult = lnd.ConnectTo walletInstance.NodeEndPoint
            match connectionResult with
            | ConnectionResult.CouldNotConnect -> failwith "could not connect"
            | _ -> ()
            return!
                lnd.OpenChannel
                    walletInstance.NodeEndPoint
                    (Money(0.002m, MoneyUnit.BTC))
                    feeRate
        }

        let! acceptChannelRes, openChannelRes = AsyncExtensions.MixedParallel2 acceptChannelTask openChannelTask
        let (channelId, _) = UnwrapResult acceptChannelRes "AcceptChannel failed"
        UnwrapResult openChannelRes "lnd.OpenChannel failed"

        // Wait for the funding transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            Thread.Sleep 500

        // Mine blocks on top of the funding transaction to make it confirmed.
        let minimumDepth = BlockHeightOffset32 6u
        bitcoind.GenerateBlocksRaw minimumDepth walletInstance.Address

        do! walletInstance.WaitForFundingConfirmed channelId

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        // Wait for lnd to realise we're offline
        do! Async.Sleep 1000
        let fundingOutPoint =
            let fundingTxId = uint256(channelInfo.FundingTxId.ToString())
            let fundingOutPointIndex = channelInfo.FundingOutPointIndex
            OutPoint(fundingTxId, fundingOutPointIndex)
        let closeChannelTask = async {
            let! connectionResult = lnd.ConnectTo walletInstance.NodeEndPoint
            match connectionResult with
            | ConnectionResult.CouldNotConnect ->
                failwith "lnd could not connect back to us"
            | _ -> ()
            do! Async.Sleep 1000
            do! lnd.CloseChannel fundingOutPoint
            return ()
        }
        let awaitCloseTask = async {
            let rec receiveEvent () = async {
                let! receivedEvent = Lightning.Network.ReceiveLightningEvent walletInstance.Node channelId
                match receivedEvent with
                | Error err ->
                    return Error (SPrintF1 "Failed to receive shutdown msg from LND: %A" err)
                | Ok event when event = IncomingChannelEvent.Shutdown ->
                    return Ok ()
                | _ -> return! receiveEvent ()
            }

            let! receiveEventRes = receiveEvent()
            UnwrapResult receiveEventRes "failed to accept close channel"

            // Wait for the closing transaction to appear in mempool
            while bitcoind.GetTxIdsInMempool().Length = 0 do
                Thread.Sleep 500

            // Mine blocks on top of the closing transaction to make it confirmed.
            let minimumDepth = BlockHeightOffset32 6u
            bitcoind.GenerateBlocksRaw minimumDepth walletInstance.Address
            return ()
        }

        let! (), () = AsyncExtensions.MixedParallel2 closeChannelTask awaitCloseTask

        return ()
    }

    [<Category("RevocationFunder")>]
    [<Test>]
    [<Timeout(200000)>]
    member __.``can revoke commitment tx (funder)``() = Async.RunSynchronously <| async {
        use! walletInstance = WalletInstance.New None None
        use bitcoind = Bitcoind.Start()
        use _electrumServer = ElectrumServer.Start bitcoind
        use! lnd = Lnd.Start bitcoind

        // As explained in the other test, geewallet cannot use coinbase outputs.
        // To work around that we mine a block to a LND instance and afterwards tell
        // it to send funds to the funder geewallet instance
        let! lndAddress = lnd.GetDepositAddress()
        let blocksMinedToLnd = BlockHeightOffset32 1u
        bitcoind.GenerateBlocksRaw blocksMinedToLnd lndAddress

        let maturityDurationInNumberOfBlocks = BlockHeightOffset32 (uint32 NBitcoin.Consensus.RegTest.CoinbaseMaturity)
        bitcoind.GenerateBlocksRaw maturityDurationInNumberOfBlocks lndAddress

        // We confirm the one block mined to LND, by waiting for LND to see the chain
        // at a height which has that block matured. The height at which the block will
        // be matured is 100 on regtest. Since we initialally mined one block for LND,
        // this will wait until the block height of LND reaches 1 (initial blocks mined)
        // plus 100 blocks (coinbase maturity). This test has been parameterized
        // to use the constants defined in NBitcoin, but you have to keep in mind that
        // the coinbase maturity may be defined differently in other coins.
        do! lnd.WaitForBlockHeight (BlockHeight.Zero + blocksMinedToLnd + maturityDurationInNumberOfBlocks)
        do! lnd.WaitForBalance (Money(50UL, MoneyUnit.BTC))

        // fund geewallet
        let geewalletAccountAmount = Money (25m, MoneyUnit.BTC)
        let! feeRate = ElectrumServer.EstimateFeeRate()
        let! _txid = lnd.SendCoins geewalletAccountAmount walletInstance.Address feeRate

        // wait for lnd's transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            Thread.Sleep 500

        // We want to make sure Geewallet consideres the money received.
        // A typical number of blocks that is almost universally considered
        // 100% confirmed, is 6. Therefore we mine 7 blocks. Because we have
        // waited for the transaction to appear in bitcoind's mempool, we
        // can assume that the first of the 7 blocks will include the
        // transaction sending money to Geewallet. The next 6 blocks will
        // bury the first block, so that the block containing the transaction
        // will be 6 deep at the end of the following call to generateBlocks.
        // At that point, the 0.25 regtest coins from the above call to sendcoins
        // are considered arrived to Geewallet.
        let consideredConfirmedAmountOfBlocksPlusOne = BlockHeightOffset32 7u
        bitcoind.GenerateBlocksRaw consideredConfirmedAmountOfBlocksPlusOne lndAddress

        let fundingAmount = Money(0.1m, MoneyUnit.BTC)
        let! transferAmount = async {
            let! accountBalance = walletInstance.WaitForBalance fundingAmount
            return TransferAmount (fundingAmount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
        }
        let! metadata = ChannelManager.EstimateChannelOpeningFee (walletInstance.Account :?> NormalUtxoAccount) transferAmount
        let! pendingChannelRes =
            Lightning.Network.OpenChannel
                walletInstance.Node
                FundeeNodeEndpoint
                transferAmount
                metadata
                walletInstance.Password
        let pendingChannel = UnwrapResult pendingChannelRes "OpenChannel failed"
        let minimumDepth = (pendingChannel :> IChannelToBeOpened).ConfirmationsRequired
        let channelId = (pendingChannel :> IChannelToBeOpened).ChannelId
        let! fundingTxIdRes = pendingChannel.Accept()
        let _fundingTxId = UnwrapResult fundingTxIdRes "pendingChannel.Accept failed"
        bitcoind.GenerateBlocksRaw (BlockHeightOffset32 minimumDepth) lndAddress

        do!
            let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
            let fundingBroadcastButNotLockedData =
                match channelInfo.Status with
                | ChannelStatus.FundingBroadcastButNotLocked fundingBroadcastButNotLockedData
                    -> fundingBroadcastButNotLockedData
                | status -> failwith (SPrintF1 "Unexpected channel status. Expected FundingBroadcastButNotLocked, got %A" status)
            let rec waitForFundingConfirmed() = async {
                let! remainingConfirmations = fundingBroadcastButNotLockedData.GetRemainingConfirmations()
                if remainingConfirmations > 0u then
                    do! Async.Sleep 1000
                    return! waitForFundingConfirmed()
                else
                    // TODO: the backend API doesn't give us any way to avoid
                    // the FundingOnChainLocationUnknown error, so just sleep
                    // to avoid the race condition. This waiting should really
                    // be implemented on the backend anyway.
                    do! Async.Sleep 10000
                    return ()
            }
            waitForFundingConfirmed()

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfo.Balance, MoneyUnit.BTC) <> fundingAmount then
            failwith "balance does not match funding amount"

        let! sendMonoHopPayment0Res =
            let transferAmount =
                let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                TransferAmount (WalletToWalletTestPayment0Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
            Lightning.Network.SendMonoHopPayment
                walletInstance.Node
                channelId
                transferAmount
        UnwrapResult sendMonoHopPayment0Res "SendMonoHopPayment failed"

        let channelInfoAfterPayment0 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment0.Balance, MoneyUnit.BTC) <> fundingAmount - WalletToWalletTestPayment0Amount then
            failwith "incorrect balance after payment 0"

        let commitmentTx = walletInstance.ChannelStore.GetCommitmentTx channelId

        let! sendMonoHopPayment1Res =
            let transferAmount =
                let accountBalance = Money(channelInfo.SpendableBalance, MoneyUnit.BTC)
                TransferAmount (WalletToWalletTestPayment1Amount.ToDecimal MoneyUnit.BTC, accountBalance.ToDecimal MoneyUnit.BTC, Currency.BTC)
            Lightning.Network.SendMonoHopPayment
                walletInstance.Node
                channelId
                transferAmount
        UnwrapResult sendMonoHopPayment1Res "SendMonoHopPayment failed"

        let channelInfoAfterPayment1 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> fundingAmount - WalletToWalletTestPayment0Amount - WalletToWalletTestPayment1Amount then
            failwith "incorrect balance after payment 1"

        let! _theftTxId = UtxoCoin.Account.BroadcastRawTransaction Currency.BTC commitmentTx

        // wait for theft transaction to appear in mempool
        while bitcoind.GetTxIdsInMempool().Length = 0 do
            do! Async.Sleep 500
        
        // mine the theft tx into a block
        bitcoind.GenerateBlocksRaw (BlockHeightOffset32 1u) lndAddress

        let! accountBalanceBeforeSpendingTheftTx =
            walletInstance.GetBalance()

        // attempt to broadcast tx which spends the theft tx
        let rec checkForClosingTx() = async {
            let! txStringOpt = Lightning.Network.CheckForClosingTx walletInstance.Node channelId
            match txStringOpt with
            | None ->
                do! Async.Sleep 500
                return! checkForClosingTx()
            | Some txString ->
                try
                    let! _txIdString = UtxoCoin.Account.BroadcastRawTransaction Currency.BTC txString
                    ()
                with
                | ex ->
                    // electrum is allowed to reject the tx because it conflicts with the penalty tx broadcast by the fundee
                    if (FSharpUtil.FindException<UtxoCoin.ElectrumServerReturningErrorException> ex).IsNone then
                        raise <| FSharpUtil.ReRaise ex
                return ()
        }
        do! checkForClosingTx()

        // give the fundee plenty of time to broadcast the penalty tx
        do! Async.Sleep 10000

        // mine enough blocks to confirm whichever tx spends the theft tx
        bitcoind.GenerateBlocksRaw (BlockHeightOffset32 minimumDepth) lndAddress

        let! accountBalanceAfterSpendingTheftTx =
            walletInstance.GetBalance()

        if accountBalanceBeforeSpendingTheftTx <> accountBalanceAfterSpendingTheftTx then
            failwithf
                "Unexpected account balance! before theft tx == %A, after theft tx == %A"
                accountBalanceBeforeSpendingTheftTx
                accountBalanceAfterSpendingTheftTx

        // give the fundee plenty of time to see that their tx was mined
        do! Async.Sleep 5000

        return ()
    }

    [<Category("RevocationFundee")>]
    [<Test>]
    [<Timeout(200000)>]
    member __.``can revoke commitment tx (fundee)``() = Async.RunSynchronously <| async {
        use! walletInstance = WalletInstance.New (Some FundeeLightningIPEndpoint) (Some FundeeAccountsPrivateKey)
        let! pendingChannelRes =
            Lightning.Network.AcceptChannel
                walletInstance.Node

        let (channelId, _) = UnwrapResult pendingChannelRes "OpenChannel failed"

        let! lockFundingRes = Lightning.Network.LockChannelFunding walletInstance.Node channelId
        UnwrapResult lockFundingRes "LockChannelFunding failed"

        let channelInfo = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfo.Balance, MoneyUnit.BTC) <> Money(0.0m, MoneyUnit.BTC) then
            failwith "incorrect balance after accepting channel"

        let! receiveMonoHopPaymentRes =
            Lightning.Network.ReceiveMonoHopPayment walletInstance.Node channelId
        UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"

        let channelInfoAfterPayment0 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment0.Balance, MoneyUnit.BTC) <> WalletToWalletTestPayment0Amount then
            failwith "incorrect balance after receiving payment 0"

        let! receiveMonoHopPaymentRes =
            Lightning.Network.ReceiveMonoHopPayment walletInstance.Node channelId
        UnwrapResult receiveMonoHopPaymentRes "ReceiveMonoHopPayment failed"

        let channelInfoAfterPayment1 = walletInstance.ChannelStore.ChannelInfo channelId
        match channelInfo.Status with
        | ChannelStatus.Active -> ()
        | status -> failwith (SPrintF1 "unexpected channel status. Expected Active, got %A" status)

        if Money(channelInfoAfterPayment1.Balance, MoneyUnit.BTC) <> WalletToWalletTestPayment0Amount + WalletToWalletTestPayment1Amount then
            failwith "incorrect balance after receiving payment 1"

        let rec checkForClosingTx() = async {
            let! txStringOpt = Lightning.Network.CheckForClosingTx walletInstance.Node channelId
            match txStringOpt with
            | None ->
                do! Async.Sleep 500
                return! checkForClosingTx()
            | Some txString ->
                let! _txIdString = UtxoCoin.Account.BroadcastRawTransaction Currency.BTC txString
                return ()
        }
        do! checkForClosingTx()

        let! _accountBalance =
            // wait for any amount of money to appear in the wallet
            let amount = Money(1.0m, MoneyUnit.Satoshi)
            walletInstance.WaitForBalance amount

        return ()
    }
    *)

