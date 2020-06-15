namespace GWallet.Backend.UtxoCoin.Lightning

open System
open System.IO
open System.Net

open NBitcoin
open DotNetLightning.Channel
open DotNetLightning.Utils
open DotNetLightning.Chain
open DotNetLightning.Crypto
open DotNetLightning.Transactions
open DotNetLightning.Serialize

open GWallet.Backend
open GWallet.Backend.UtxoCoin.Lightning.Util

open Newtonsoft.Json

type FeatureBitJsonConverter() =
    inherit JsonConverter<FeatureBit>()

    override this.ReadJson(reader: JsonReader, _: Type, _: FeatureBit, _: bool, serializer: JsonSerializer) =
        let serializedFeatureBit = serializer.Deserialize<string> reader
        Unwrap (FeatureBit.TryParse serializedFeatureBit) "error decoding feature bit"

    override this.WriteJson(writer: JsonWriter, state: FeatureBit, serializer: JsonSerializer) =
        serializer.Serialize(writer, state.ToString())

type IPAddressJsonConverter() =
    inherit JsonConverter<IPAddress>()

    override this.ReadJson(reader: JsonReader, _: Type, _: IPAddress, _: bool, serializer: JsonSerializer) =
        let serializedIPAddress = serializer.Deserialize<string> reader
        IPAddress.Parse serializedIPAddress

    override this.WriteJson(writer: JsonWriter, state: IPAddress, serializer: JsonSerializer) =
        serializer.Serialize(writer, state.ToString())

type IPEndPointJsonConverter() =
    inherit JsonConverter<IPEndPoint>()

    override this.ReadJson(reader: JsonReader, _: Type, _: IPEndPoint, _: bool, serializer: JsonSerializer) =
        assert (reader.TokenType = JsonToken.StartArray)
        reader.Read() |> ignore
        let ip = serializer.Deserialize<IPAddress> reader
        reader.Read() |> ignore
        let port = serializer.Deserialize<int32> reader
        assert (reader.TokenType = JsonToken.EndArray)
        reader.Read() |> ignore
        IPEndPoint (ip, port)

    override this.WriteJson(writer: JsonWriter, state: IPEndPoint, serializer: JsonSerializer) =
        writer.WriteStartArray()
        serializer.Serialize(writer, state.Address)
        serializer.Serialize(writer, state.Port)
        writer.WriteEndArray()

module JsonMarshalling =
    let SerializerSettings: JsonSerializerSettings =
        let settings = Marshalling.DefaultSettings ()
        let ipAddressConverter = IPAddressJsonConverter()
        let ipEndPointConverter = IPEndPointJsonConverter()
        let featureBitConverter = FeatureBitJsonConverter()
        settings.Converters.Add ipAddressConverter
        settings.Converters.Add ipEndPointConverter
        settings.Converters.Add featureBitConverter
        NBitcoin.JsonConverters.Serializer.RegisterFrontConverters settings
        settings

