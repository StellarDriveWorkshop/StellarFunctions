using System;
using System.Collections.Generic;
using System.Linq;

namespace StellarFunctions;

internal sealed class FunctionParameter
{
    internal readonly Guid Id;
    internal readonly string Name;
    internal readonly int Index;
    internal readonly double? Default;
    internal FunctionParameter(Guid id, string name, int index, double? fallback)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 64 || index < 1 || index > 3 ||
            (fallback.HasValue && !double.IsFinite(fallback.Value))) throw new FunctionFault("BAD PARAM");
        Id=id; Name=name; Index=index; Default=fallback;
    }
}

internal enum FunctionOp { Parameter, Constant, Binary, Unary, Memory, RawDelta, Nested, External, MoreMathBinary, MoreMathUnary }

internal sealed class FunctionNode
{
    internal readonly Guid Id;
    internal readonly FunctionOp Op;
    internal readonly string Operation;
    private readonly int[] _inputs;
    private readonly Guid[] _bindings;
    internal readonly double Initial;
    internal readonly Guid Reference;
    internal readonly byte WiredMask;
    internal int Input(int slot) => slot < _inputs.Length ? _inputs[slot] : -1;
    internal int InputCount => _inputs.Length;
    internal Guid Binding(int slot) => slot < _bindings.Length ? _bindings[slot] : Guid.Empty;
    internal FunctionNode(Guid id, FunctionOp op, int[] inputs=null, string operation=null, double initial=0,
        Guid reference=default, Guid[] bindings=null, byte wiredMask=0)
    {
        if (id==Guid.Empty || !double.IsFinite(initial)) throw new FunctionFault("BAD NODE");
        Id=id; Op=op; _inputs=inputs?.ToArray() ?? Array.Empty<int>(); Operation=operation;
        Initial=initial; Reference=reference; _bindings=bindings?.ToArray() ?? Array.Empty<Guid>(); WiredMask=wiredMask;
    }
}

internal sealed class FunctionGraph
{
    internal readonly Guid Id;
    internal readonly string Name;
    internal readonly string Structure;
    private readonly FunctionNode[] _nodes;
    private readonly FunctionParameter[] _parameters;
    internal readonly int Result;
    internal int Count => _nodes.Length;
    internal int ParameterCount => _parameters.Length;
    internal FunctionNode Node(int index) => _nodes[index];
    internal FunctionParameter Parameter(int index) => _parameters[index];
    internal IEnumerable<FunctionParameter> Parameters => _parameters.ToArray();
    internal FunctionParameter FindParameter(Guid id){for(int i=0;i<_parameters.Length;i++)if(_parameters[i].Id==id)return _parameters[i];return null;}
    internal bool HasParameter(Guid id){for(int i=0;i<_parameters.Length;i++)if(_parameters[i].Id==id)return true;return false;}
    internal FunctionGraph(Guid id, string name, FunctionParameter[] parameters, FunctionNode[] nodes, int result, string structure)
    {
        if(id==Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length>64 || nodes==null || nodes.Length<1 || nodes.Length>256 ||
            result<0 || result>=nodes.Length) throw new FunctionFault("BAD DEFINITION");
        _parameters=(parameters ?? Array.Empty<FunctionParameter>()).OrderBy(p=>p.Index).ToArray();
        if(_parameters.Length>3 || !_parameters.Select(p=>p.Index).SequenceEqual(Enumerable.Range(1,_parameters.Length)) ||
            _parameters.Select(p=>p.Id).Distinct().Count()!=_parameters.Length) throw new FunctionFault("BAD PARAM INDEX");
        _nodes=nodes.ToArray();
        if(_nodes.Select(n=>n.Id).Distinct().Count()!=_nodes.Length) throw new FunctionFault("DUPLICATE NODE");
        Id=id; Name=name; Result=result; Structure=structure ?? string.Empty;
        for(int i=0;i<_nodes.Length;i++)for(int k=0;k<_nodes[i].InputCount;k++)
            if(_nodes[i].Input(k)<-1 || _nodes[i].Input(k)>=_nodes.Length) throw new FunctionFault("BAD EDGE");
        var marks=new byte[_nodes.Length];
        for(int i=0;i<_nodes.Length;i++) CheckAcyclic(i,marks);
    }
    private void CheckAcyclic(int index, byte[] marks)
    {
        if(marks[index]==2)return;
        if(marks[index]==1)throw new FunctionFault("WIRE LOOP");
        marks[index]=1;
        // Memory emits the previous value. Its write inputs belong to the later
        // commit phase, so they are not combinational dependencies of this output.
        if(_nodes[index].Op!=FunctionOp.Memory)
            for(int i=0;i<_nodes[index].InputCount;i++) if(_nodes[index].Input(i)>=0) CheckAcyclic(_nodes[index].Input(i),marks);
        marks[index]=2;
    }
}

internal sealed class FunctionFault : Exception
{
    internal FunctionFault(string status) : base(status) { }
}

internal sealed class FunctionEvaluation
{
    internal readonly double Output;
    internal readonly string Error;
    internal readonly Dictionary<string,double> ProposedState;
    internal readonly int Operations;
    internal FunctionEvaluation(double output, string error, Dictionary<string,double> proposed, int operations)
        {Output=output; Error=error; ProposedState=proposed; Operations=operations;}
}

// Pure numeric executor. It never reads or writes a Unity object, live body
// part, native wire value, global channel, or another Call's mutable buffers.
internal static class FunctionEvaluator
{
    internal const int MaxExpandedOperations=4096;
    internal static FunctionEvaluation Evaluate(FunctionGraph graph, Func<FunctionGraph,Guid,FunctionGraph> resolve,
        IReadOnlyDictionary<Guid,double> arguments, IReadOnlyDictionary<string,double> oldState,
        Func<Guid,double> external, double dt)
    {
        Context context=null;
        try
        {
            context=new Context(resolve,oldState,external,dt);
            if(!double.IsFinite(dt) || dt<0)throw new FunctionFault("BAD DELTA TIME");
            double result=context.Run(graph,arguments,string.Empty,0,new HashSet<Guid>());
            if(!double.IsFinite(result))throw new FunctionFault("NONFINITE OUTPUT");
            return new FunctionEvaluation(result,null,context.Proposed,context.Operations);
        }
        catch(FunctionFault error){return new FunctionEvaluation(0,error.Message,null,context?.Operations??0);}
        catch(Exception){return new FunctionEvaluation(0,"EVALUATION ERROR",null,context?.Operations??0);}
    }
    internal static FunctionEvaluation Evaluate(FunctionGraph graph, Func<Guid,FunctionGraph> resolve,
        IReadOnlyDictionary<Guid,double> arguments, IReadOnlyDictionary<string,double> oldState,
        Func<Guid,double> external, double dt)
        => Evaluate(graph, resolve != null ? (caller, id) => resolve(id) : null, arguments, oldState, external, dt);

    private sealed class Context
    {
        private readonly Func<FunctionGraph,Guid,FunctionGraph> _resolve;
        private readonly IReadOnlyDictionary<string,double> _old;
        private readonly Func<Guid,double> _external;
        private readonly double _dt;
        internal readonly Dictionary<string,double> Proposed;
        internal int Operations;
        internal Context(Func<FunctionGraph,Guid,FunctionGraph> resolve, IReadOnlyDictionary<string,double> old,Func<Guid,double> external,double dt)
        {
            _resolve=resolve; _old=old ?? new Dictionary<string,double>(); _external=external; _dt=dt;
            Proposed=new Dictionary<string,double>();
            foreach(var pair in _old)
            {
                if(pair.Key==null || pair.Key.Length>600 || !double.IsFinite(pair.Value))throw new FunctionFault("BAD INSTANCE STATE");
                Proposed[pair.Key]=pair.Value;
            }
            if(Proposed.Count>4096)throw new FunctionFault("STATE LIMIT");
        }
        internal double Run(FunctionGraph graph,IReadOnlyDictionary<Guid,double> args,string path,int depth,HashSet<Guid> stack)
        {
            if(graph==null)throw new FunctionFault("MISSING FUNCTION");
            if(depth>=8)throw new FunctionFault("NESTING LIMIT");
            if(!stack.Add(graph.Id))throw new FunctionFault("RECURSION");
            try
            {
                if((Operations+=graph.Count)>MaxExpandedOperations)throw new FunctionFault("OPERATION LIMIT");
                var frame=new Frame(this,graph,args,path,depth,stack);
                double output=frame.Value(graph.Result);
                // All reads use the immutable old snapshot, including cycles through
                // several Memory nodes. Proposed writes are committed by the caller
                // only if this entire nested subtree succeeds.
                for(int i=0;i<graph.Count;i++)
                {
                    FunctionNode node=graph.Node(i);
                    if(node.Op!=FunctionOp.Memory)continue;
                    double enable=frame.Value(node.Input(1));
                    if(enable>=1)Write(path+node.Id.ToString("N"),frame.Value(node.Input(0)));
                }
                return output;
            }
            finally {stack.Remove(graph.Id);}
        }
        private double Read(string key,double initial)=>_old.TryGetValue(key,out double value)?value:initial;
        private void Write(string key,double value)
        {
            if(!double.IsFinite(value))throw new FunctionFault("NONFINITE STATE");
            if(!Proposed.ContainsKey(key) && Proposed.Count>=4096)throw new FunctionFault("STATE LIMIT");
            Proposed[key]=value;
        }
        private sealed class Frame
        {
            private readonly Context _c;
            private readonly FunctionGraph _g;
            private readonly IReadOnlyDictionary<Guid,double> _args;
            private readonly string _path;
            private readonly int _depth;
            private readonly HashSet<Guid> _stack;
            private readonly double[] _values;
            private readonly byte[] _marks;
            internal Frame(Context c,FunctionGraph g,IReadOnlyDictionary<Guid,double> args,string path,int depth,HashSet<Guid> stack)
            {_c=c;_g=g;_args=args;_path=path;_depth=depth;_stack=stack;_values=new double[g.Count];_marks=new byte[g.Count];}
            internal double Value(int i)
            {
                if(i<0)return 0;
                if(_marks[i]==2)return _values[i];
                if(_marks[i]==1)throw new FunctionFault("WIRE LOOP");
                _marks[i]=1;FunctionNode n=_g.Node(i);string key=_path+n.Id.ToString("N");double value;
                switch(n.Op)
                {
                    case FunctionOp.Parameter:
                        FunctionParameter p=_g.FindParameter(n.Reference);
                        if(p==null)throw new FunctionFault("MISSING PARAM");
                        value=_args!=null && _args.TryGetValue(p.Id,out double input)?input:p.Default??0;break;
                    case FunctionOp.Constant:value=n.Initial;break;
                    case FunctionOp.Memory:value=_c.Read(key,n.Initial);break;
                    case FunctionOp.Binary:value=Binary(n.Operation,Value(n.Input(0)),Value(n.Input(1)));break;
                    case FunctionOp.Unary:
                        double x=Value(n.Input(0));
                        if(n.Operation=="Delta") {value=_c._dt==0?0:(x-_c.Read(key,0))/_c._dt;_c.Write(key,x);}
                        else value=Unary(n.Operation,x);
                        break;
                    case FunctionOp.RawDelta:
                        double sample=Value(n.Input(0));value=sample-_c.Read(key,0);_c.Write(key,sample);break;
                    case FunctionOp.MoreMathBinary:
                    case FunctionOp.MoreMathUnary:
                        bool binary=n.Op==FunctionOp.MoreMathBinary;int operation=(int)n.Initial;
                        int fields=binary?MoreMathOperations.BinaryFields(operation):MoreMathOperations.UnaryFields(operation);
                        string previousKey=(fields&2)!=0?key+"/p":null,elapsedKey=(fields&4)!=0?key+"/t":null;
                        double prior=(fields&1)!=0?_c.Read(key,0):0,previous=previousKey==null?0:_c.Read(previousKey,0),elapsed=elapsedKey==null?0:_c.Read(elapsedKey,0);
                        MoreMathStep step=binary?MoreMathOperations.Binary(operation,Value(n.Input(0)),Value(n.Input(1)),prior,previous,elapsed,_c._dt):
                            MoreMathOperations.Unary(operation,Value(n.Input(0)),prior,previous,_c._dt);
                        value=step.Output;
                        if((fields&1)!=0)_c.Write(key,value);
                        if(previousKey!=null)_c.Write(previousKey,step.Previous);
                        if(elapsedKey!=null)_c.Write(elapsedKey,step.Elapsed);
                        break;
                    case FunctionOp.External:
                        if(_c._external==null)throw new FunctionFault("MISSING SOURCE");
                        value=_c._external(n.Reference);break;
                    case FunctionOp.Nested:
                        FunctionGraph target=_c._resolve?.Invoke(_g,n.Reference);
                        if(target==null)throw new FunctionFault("MISSING FUNCTION");
                        var supplied=new Dictionary<Guid,double>();
                        for(int k=0;k<3;k++)
                        {
                            Guid binding=n.Binding(k);
                            if(binding==Guid.Empty)continue;
                            if(!target.HasParameter(binding))throw new FunctionFault("PARAM REMOVED");
                            if((n.WiredMask&(1<<k))!=0)supplied.Add(binding,Value(n.Input(k)));
                        }
                        value=_c.Run(target,supplied,key+"/",_depth+1,_stack);break;
                    default:throw new FunctionFault("UNSUPPORTED NODE");
                }
                if(!double.IsFinite(value))throw new FunctionFault("NONFINITE OUTPUT");
                _values[i]=value;_marks[i]=2;return value;
            }
        }
    }
    internal static double Binary(string operation,double a,double b)
    {
        switch(operation)
        {
            case "Add":return a+b;case "Subtract":return a-b;case "Multiply":return a*b;
            case "Divide":return b==0?0:a/b;case "Min":return Math.Min(a,b);case "Max":return Math.Max(a,b);
            case "Atan2":return Math.Atan2(a,b);case "LesserThan":return a<b?1:0;case "GreaterThan":return a>b?1:0;case "Equals":return a==b?1:0;
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }
    internal static double Unary(string operation,double x)
    {
        switch(operation)
        {
            case "Negation":return -x;case "SquareRoot":return x<0?0:Math.Sqrt(x);case "Abs":return Math.Abs(x);
            case "Sin":return Math.Sin(x);case "Cos":return Math.Cos(x);case "Tan":return Math.Tan(x);
            case "Asin":return Math.Asin(Math.Max(-1,Math.Min(1,x)));case "Acos":return Math.Acos(Math.Max(-1,Math.Min(1,x)));case "Atan":return Math.Atan(x);
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }
}
