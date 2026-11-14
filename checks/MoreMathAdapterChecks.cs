// Differential checks against the actual pinned MoreMath dispatcher.
// Native services, parts and groups are supplied; no world or transport starts.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using UnityEngine;

namespace StellarFunctions;
internal static class MoreMathAdapterChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var functions=typeof(FunctionCallState).Assembly;
        var adapter=functions.GetType("StellarFunctions.MoreMathCompatibility",false);
        var moreMath=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="MoreMath");
        if(moreMath==null){check(adapter!=null&&!(bool)AccessTools.Property(adapter,"Ready").GetValue(null),"Functions keeps MoreMath optional when it is not installed");return;}
        check(adapter!=null&&(bool)AccessTools.Property(adapter,"Ready").GetValue(null),"reviewed MoreMath 1.3 dispatcher adapter installed");
        var prefix=AccessTools.Method(moreMath.GetType("MoreMath.SignalConnectionsServerUpdaterPatch",true),"Prefix");
        var operations=(IList)Activator.CreateInstance(prefix.GetParameters()[2].ParameterType);
        Type operationType=prefix.GetParameters()[2].ParameterType.GetGenericArguments()[0];
        var service=new GameObject("Private MoreMath differential services"){tag="ServiceObject"};
        var ships=service.AddComponent<ShipsServerTracker>();var connections=service.AddComponent<ConnectionsServerTracker>();
        const uint shipId=482001;
        var state=new ShipState(shipId,shipId,new PartSettingsProvider());
        var ship=(TrackedShipServer)FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
        AccessTools.Field(typeof(TrackedShip),"<State>k__BackingField").SetValue(ship,state);
        ((IDictionary)AccessTools.Field(typeof(ShipsServerTracker),"_shipsDict").GetValue(ships)).Add(shipId,ship);
        var settings=Resources.FindObjectsOfTypeAll<PartSettings>().Where(p=>p!=null).GroupBy(p=>p.id).ToDictionary(g=>g.Key,g=>g.First());
        var edges=new List<SocketConnection>();
        void Add(ushort id,PartSettings asset,IStatefulPartState value)=>state.AddStatefulPartWithoutInit(
            new StatefulPart(id,asset,new FloatingPartPlacement(Vector3.zero,PartOrientation.Forward,PartOrientation.Up)){State=value});
        void Connect(ushort from,ushort to,byte socket)
        {var edge=new SocketConnection(new EntityPartSocket(shipId,from,0),new EntityPartSocket(shipId,to,socket),SocketConnectionType.Cable);connections.SignalConnectionGroups.OnConnectionCreate(edge);edges.Add(edge);}
        try
        {
            Add(1,settings[46],new ConstantState());Add(2,settings[46],new ConstantState());
            Type parameter=functions.GetType("StellarFunctions.FunctionParameter"),node=functions.GetType("StellarFunctions.FunctionNode"),
                graph=functions.GetType("StellarFunctions.FunctionGraph"),op=functions.GetType("StellarFunctions.FunctionOp");
            var evaluate=functions.GetType("StellarFunctions.FunctionEvaluator").GetMethods(BindingFlags.Static|BindingFlags.NonPublic)
                .Single(m=>m.Name=="Evaluate"&&m.GetParameters()[1].ParameterType.GetGenericArguments().Length==2);
            var helper=functions.GetType("StellarFunctions.MoreMathOperations");
            float dt=.1f;
            double[,] samples={{0,0},{1,0},{1,.2},{1,.2},{1,.2},{0,.2},{0,.2},{0,.2},{0,.2},
                {1,0},{-1,2},{-1,2},{0,0},{1,0},{-1,0},{0,0},{.5,2},{-.5,2},{2,-1},{0,-1},{-1,.2},{2,3}};
            for(int kind=0;kind<2;kind++)for(int code=10;code<=(kind==0?30:17);code++)
            {
                bool binary=kind==0;ushort id=(ushort)(100+kind*100+code);
                Add(id,settings[(ushort)(binary?45:54)],binary?(IStatefulPartState)new MathGateState{Operation=(MathGateOperation)code}:
                    new SignalProcessorState{Operation=(SignalProcessorOperation)code});
                Connect(1,id,0);if(binary)Connect(2,id,1);
                object operation=Activator.CreateInstance(operationType);
                AccessTools.Field(operationType,"Operation").SetValue(operation,Enum.Parse(AccessTools.Field(operationType,"Operation").FieldType,"EvaluatePart"));
                AccessTools.Field(operationType,"Part").SetValue(operation,new PartContext(shipId,id));
                operations.Clear();operations.Add(operation);
                Guid aId=Guid.NewGuid(),bId=Guid.NewGuid();
                Array parameters=Array.CreateInstance(parameter,binary?2:1);
                object Make(Type type,params object[] args)=>Activator.CreateInstance(type,BindingFlags.NonPublic|BindingFlags.Instance,null,args,null);
                parameters.SetValue(Make(parameter,aId,"a",1,(double?)0),0);
                if(binary)parameters.SetValue(Make(parameter,bId,"b",2,(double?)0),1);
                Array nodes=Array.CreateInstance(node,binary?3:2);
                nodes.SetValue(Make(node,Guid.NewGuid(),Enum.Parse(op,"Parameter"),null,null,0d,aId,Array.Empty<Guid>(),(byte)0),0);
                if(binary)nodes.SetValue(Make(node,Guid.NewGuid(),Enum.Parse(op,"Parameter"),null,null,0d,bId,Array.Empty<Guid>(),(byte)0),1);
                string name=(string)AccessTools.Method(helper,binary?"BinaryName":"UnaryName").Invoke(null,new object[]{code});
                nodes.SetValue(Make(node,Guid.NewGuid(),Enum.Parse(op,binary?"MoreMathBinary":"MoreMathUnary"),binary?new[]{0,1}:new[]{0},name,(double)code,Guid.Empty,Array.Empty<Guid>(),(byte)0),binary?2:1);
                object body=Make(graph,Guid.NewGuid(),"Native parity",parameters,nodes,binary?2:1,"parity");
                var history=new Dictionary<string,double>();
                for(int sample=0;sample<samples.GetLength(0);sample++)
                {
                    double a=samples[sample,0],b=samples[sample,1];
                    ship.UpdatePartState(1,new ConstantState{ConstantValue=a});ship.UpdatePartState(2,new ConstantState{ConstantValue=b});
                    foreach(var group in connections.SignalConnectionGroups.ConnectionGroups.Values.ToArray())connections.UpdateSignalGroupValue(group.Id);
                    prefix.Invoke(null,new object[]{null,dt,operations,ships,connections});
                    ship.TryGetStatefulPart(id,out var physical);
                    double native=binary?((MathGateState)physical.State).Output:((SignalProcessorState)physical.State).Output;
                    var args=new Dictionary<Guid,double>{{aId,a}};if(binary)args[bId]=b;
                    object result=evaluate.Invoke(null,new object[]{body,null,args,history,null,(double)dt});
                    string error=(string)AccessTools.Field(result.GetType(),"Error").GetValue(result);
                    double actual=(double)AccessTools.Field(result.GetType(),"Output").GetValue(result);
                    var proposed=(Dictionary<string,double>)AccessTools.Field(result.GetType(),"ProposedState").GetValue(result);
                    if(double.IsNaN(native)||double.IsInfinity(native))
                        check(error!=null&&actual==0&&proposed==null,name+" nonfinite native result fails closed without state commit");
                    else
                    {
                        check(error==null&&Math.Abs(actual-native)<=1e-9*Math.Max(1,Math.Abs(native)),name+" native/evaluator parity at step "+sample+" (native="+native+", virtual="+actual+", error="+error+")");
                        history=JsonConvert.DeserializeObject<Dictionary<string,double>>(JsonConvert.SerializeObject(proposed));
                    }
                }
            }
            // Installed-unused dispatch overhead: same 128 ordinary Power
            // parts, only this adapter's dispatcher hook toggled. No Call or
            // compiled definition exists in this supplied-state workload.
            ship.UpdatePartState(1,new ConstantState{ConstantValue=2});ship.UpdatePartState(2,new ConstantState{ConstantValue=3});
            operations.Clear();
            for(ushort id=1000;id<1128;id++)
            {
                Add(id,settings[45],new MathGateState{Operation=(MathGateOperation)10});Connect(1,id,0);Connect(2,id,1);
                object item=Activator.CreateInstance(operationType);
                AccessTools.Field(operationType,"Operation").SetValue(item,Enum.Parse(AccessTools.Field(operationType,"Operation").FieldType,"EvaluatePart"));
                AccessTools.Field(operationType,"Part").SetValue(item,new PartContext(shipId,id));operations.Add(item);
            }
            foreach(var group in connections.SignalConnectionGroups.ConnectionGroups.Values.ToArray())connections.UpdateSignalGroupValue(group.Id);
            var invokeArgs=new object[]{null,dt,operations,ships,connections};
            double Sample(bool enabled)
            {
                AccessTools.Method(adapter,enabled?"Install":"Uninstall").Invoke(null,null);
                for(int warm=0;warm<32;warm++)prefix.Invoke(null,invokeArgs);
                long started=Stopwatch.GetTimestamp();
                for(int repeat=0;repeat<200;repeat++)prefix.Invoke(null,invokeArgs);
                return (Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency/200;
            }
            var baseline=new double[3];var candidate=new double[3];
            try
            {
                for(int pair=0;pair<3;pair++)
                {
                    if(pair==1){candidate[pair]=Sample(true);baseline[pair]=Sample(false);}
                    else{baseline[pair]=Sample(false);candidate[pair]=Sample(true);}
                }
            }
            finally{AccessTools.Method(adapter,"Install").Invoke(null,null);}
            double added=Enumerable.Range(0,3).Select(i=>candidate[i]-baseline[i]).OrderBy(v=>v).ElementAt(1);
            double baselineMedian=baseline.OrderBy(v=>v).ElementAt(1);
            check(added<=Math.Max(.25,baselineMedian*.05),"MoreMath adapter installed-unused dispatch cost fits the predeclared 0.25 ms/128-parts absolute budget");
            check(Enumerable.Range(1000,128).All(id=>{ship.TryGetStatefulPart((ushort)id,out var p);return ((MathGateState)p.State).Output==8;}),
                "dispatch-cost controls preserve ordinary MoreMath outputs");
            string output=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"menu-probe-output.txt")).Trim();
            File.WriteAllText(Path.Combine(output,"moremath-dispatch-cost.json"),JsonConvert.SerializeObject(new
                {parts=128,repeats=200,pairOrder="AB/BA/AB",baselineMs=baseline,candidateMs=candidate,addedMedianMs=added,
                    control="only Functions MoreMath dispatcher hook removed",scope="supplied native parts; actual MoreMath prefix; no world/network",allocationsMeasured=false}));
        }
        finally
        {
            foreach(var edge in edges)connections.SignalConnectionGroups.OnConnectionDelete(edge);
            UnityEngine.Object.DestroyImmediate(service);
        }
    }
}
