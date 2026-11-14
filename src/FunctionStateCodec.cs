using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StellarFunctions;

internal static class FunctionStateCodec
{
    private const int Schema=1;
    private const int SizedCallSchema=2;
    private static readonly JsonSerializerSettings Settings=new JsonSerializerSettings
        {TypeNameHandling=TypeNameHandling.None,MaxDepth=20,Culture=System.Globalization.CultureInfo.InvariantCulture};
    internal static string Encode(FunctionPartState state,bool privateData)
    {
        Validate(state);
        JObject fields=JObject.FromObject(state,JsonSerializer.Create(Settings));
        if(!privateData){fields.Remove("RuntimeJson");fields.Remove("StructureKey");fields.Remove("NodeIdentities");}
        string json=new JObject{["schema"]=state is FunctionCallState?SizedCallSchema:Schema,["kind"]=Kind(state),["state"]=fields}.ToString(Formatting.None);
        if(json.Length>(privateData?524288:65536))throw new InvalidDataException("Function state envelope exceeds its size limit.");
        return json;
    }
    internal static FunctionPartState Decode(string json,Type expected,bool privateData)
    {
        if(json==null||json.Length>(privateData?524288:65536))throw new InvalidDataException("Function state envelope exceeds its size limit.");
        JObject root=NativeFunctionCompiler.ParseObject(json,privateData?524288:65536);
        int? schema=(int?)root["schema"];
        if(schema!=Schema&&schema!=SizedCallSchema)throw new InvalidDataException("Unsupported function state schema; refusing to discard saved fields.");
        Type type=(string)root["kind"]=="call"?typeof(FunctionCallState):(string)root["kind"]=="param"?typeof(FunctionParamState):
            (string)root["kind"]=="return"?typeof(FunctionReturnState):(string)root["kind"]=="transmitter"?typeof(FunctionTransmitterState):null;
        if(type==null||type!=expected||root["state"] is not JObject fields)throw new InvalidDataException("Function state kind does not match its content part.");
        if(schema==SizedCallSchema&&(type!=typeof(FunctionCallState)||fields.Property("InputCount")==null))
            throw new InvalidDataException("Sized Call state is missing its input count or has an invalid kind.");
        // Record initializers generate fresh identities for newly built parts.
        // Missing saved fields must never accidentally take that construction
        // path and silently replace an existing definition or instance.
        string identity=type==typeof(FunctionParamState)?"ParamId":type==typeof(FunctionReturnState)?"DefinitionId":type==typeof(FunctionTransmitterState)?"TransmitterId":"InstanceId";
        if(fields[identity]?.Type!=JTokenType.String||!Guid.TryParse((string)fields[identity],out Guid stable)||stable==Guid.Empty)
            throw new InvalidDataException("Function state is missing its saved identity.");
        string[] required=type==typeof(FunctionParamState)?new[]{"Name","Index","DefaultValue"}:
            type==typeof(FunctionReturnState)?new[]{"Name","Sources"}:type==typeof(FunctionTransmitterState)?new[]{"Summary"}:new[]{"SelectedFunctionId","Slot0","Slot1","Slot2"};
        if(required.Any(name=>fields.Property(name)==null)||privateData&&
            (type==typeof(FunctionReturnState)&&fields.Property("NodeIdentities")==null||
             type==typeof(FunctionCallState)&&(fields.Property("RuntimeJson")==null||fields.Property("StructureKey")==null)))
            throw new InvalidDataException("Function state is missing required saved fields.");
        if(fields.Property("Broadcast")!=null&&fields["Broadcast"].Type!=JTokenType.Boolean)
            throw new InvalidDataException("Invalid Return broadcast field.");
        if(type==typeof(FunctionCallState)&&fields.Property("InputCount")!=null&&
            (fields["InputCount"].Type!=JTokenType.Integer||!int.TryParse(fields["InputCount"].ToString(),System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,out int inputCount)||inputCount<1||inputCount>3))
            throw new InvalidDataException("Invalid Call input count.");
        if(!privateData){fields.Remove("RuntimeJson");fields.Remove("StructureKey");fields.Remove("NodeIdentities");}
        FunctionPartState state=(FunctionPartState)fields.ToObject(type,JsonSerializer.Create(Settings));
        Validate(state);return state;
    }
    internal static Type StateType(ushort settings)=>settings==62005?typeof(FunctionCallState):settings==62006?typeof(FunctionParamState):settings==62007?typeof(FunctionReturnState):settings==62009?typeof(FunctionTransmitterState):null;
    private static string Kind(FunctionPartState state)=>state is FunctionCallState?"call":state is FunctionParamState?"param":state is FunctionReturnState?"return":state is FunctionTransmitterState?"transmitter":throw new InvalidDataException("Unknown function state kind.");
    private static void Text(string value,int max,bool required=false)
    {
        if(value==null||value.Length>max||(required&&string.IsNullOrWhiteSpace(value))||value.Any(char.IsControl))throw new InvalidDataException("Invalid function text field.");
    }
    private static void OptionalId(string value){Text(value??"",64);if(!string.IsNullOrEmpty(value)&&(!Guid.TryParse(value,out Guid id)||id==Guid.Empty))throw new InvalidDataException("Invalid function identity reference.");}
    internal static void Validate(FunctionPartState state)
    {
        if(state==null||state.StableId==Guid.Empty)throw new InvalidDataException("Function part has an empty identity.");
        if(state is FunctionParamState p){Text(p.Name,64,true);if(p.Index<1||p.Index>3||(p.DefaultValue.HasValue&&!double.IsFinite(p.DefaultValue.Value)))throw new InvalidDataException("Invalid Param fields.");}
        else if(state is FunctionReturnState r)
        {
            Text(r.Name,64,true);Text(r.Status,64);NativeFunctionCompiler.ParseObject(r.Sources,32768);NativeFunctionCompiler.ParseObject(r.NodeIdentities,65536);
        }
        else if(state is FunctionTransmitterState t)
        {
            Text(t.Summary,128);
        }
        else if(state is FunctionCallState c)
        {
            OptionalId(c.SelectedFunctionId);OptionalId(c.Slot0);OptionalId(c.Slot1);OptionalId(c.Slot2);Text(c.SelectedName,64);Text(c.Status,64);Text(c.StructureKey,64);
            OptionalId(c.PendingFunctionId);Text(c.PendingFunctionName,64);
            Text(c.Input0Label,128);Text(c.Input1Label,128);Text(c.Input2Label,128);
            Text(c.ResizeFeedback,128);
            if(c.InputCount<1||c.InputCount>3)throw new InvalidDataException("Invalid Call input count.");
            if(c.ListeningMask>7||!double.IsFinite(c.Output)||!double.IsFinite(c.LastInput0)||!double.IsFinite(c.LastInput1)||!double.IsFinite(c.LastInput2)||c.RuntimeJson==null||c.RuntimeJson.Length>262144)
                throw new InvalidDataException("Invalid Call runtime fields.");
            var history=NativeFunctionCompiler.ParseObject(c.RuntimeJson,262144);
            if(history.Count>4096||history.Properties().Any(p=>p.Name.Length>600||p.Value.Type!=JTokenType.Float&&p.Value.Type!=JTokenType.Integer||!double.IsFinite((double)p.Value)))
                throw new InvalidDataException("Invalid Call history.");
        }
    }
}
