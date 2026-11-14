using System;

namespace StellarFunctions;

// Pure numeric MoreMath 1.3.0.0 operations. Stateful history is supplied by
// the caller so each virtual Call can keep an isolated copy.
internal readonly struct MoreMathStep
{
    internal readonly double Output;
    internal readonly double Previous;
    internal readonly double Elapsed;

    internal MoreMathStep(double output, double previous=0, double elapsed=0)
    {
        Output=output;
        Previous=previous;
        Elapsed=elapsed;
    }
}

internal static class MoreMathOperations
{
    internal static string BinaryName(int operation)
    {
        switch(operation)
        {
            case 10:return "MoreMath.Power";
            case 11:return "MoreMath.Round";
            case 12:return "MoreMath.LesserSameThan";
            case 13:return "MoreMath.GreaterSameThan";
            case 14:return "MoreMath.Rest";
            case 15:return "MoreMath.Clamp";
            case 16:return "MoreMath.RS";
            case 17:return "MoreMath.SR";
            case 18:return "MoreMath.OnDelay";
            case 19:return "MoreMath.OffDelay";
            case 20:return "MoreMath.Log";
            case 21:return "MoreMath.Or";
            case 22:return "MoreMath.Xor";
            case 23:return "MoreMath.Nand";
            case 24:return "MoreMath.Nor";
            case 25:return "MoreMath.NotEuqal";
            case 26:return "MoreMath.Hypot";
            case 27:return "MoreMath.Deadband";
            case 28:return "MoreMath.SchmittTrigger";
            case 29:return "MoreMath.Timer";
            case 30:return "MoreMath.Counter";
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }

    internal static string UnaryName(int operation)
    {
        switch(operation)
        {
            case 10:return "MoreMath.Floor";
            case 11:return "MoreMath.Toggle";
            case 12:return "MoreMath.OneWay";
            case 13:return "MoreMath.Ceil";
            case 14:return "MoreMath.Not";
            case 15:return "MoreMath.Sign";
            case 16:return "MoreMath.Integrator";
            case 17:return "MoreMath.Exp";
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }

    // State-field bitmask: output=1, previous input/direction=2, elapsed=4.
    internal static int BinaryFields(int operation)
    {
        switch(operation)
        {
            case 16:case 17:case 28:case 29:return 1;
            case 18:case 19:return 7;
            case 30:return 3;
            case 10:case 11:case 12:case 13:case 14:case 15:
            case 20:case 21:case 22:case 23:case 24:case 25:
            case 26:case 27:return 0;
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }

    internal static int UnaryFields(int operation)
    {
        switch(operation)
        {
            case 11:return 3;
            case 16:return 1;
            case 10:case 12:case 13:case 14:case 15:case 17:return 0;
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }

    internal static MoreMathStep Binary(int operation,double a,double b,double output,double previous,double elapsed,double dt)
    {
        switch(operation)
        {
            case 10:return new MoreMathStep(Math.Pow(a,b));
            case 11:
                double scale=Math.Pow(10.0,b);
                return new MoreMathStep(Math.Round(scale*a)/scale);
            case 12:return new MoreMathStep(a<b||a==b?1:0);
            case 13:return new MoreMathStep(a>b||a==b?1:0);
            case 14:return new MoreMathStep(b==0?0:a%b);
            case 15:return new MoreMathStep(Math.Clamp(a,-Math.Abs(b),Math.Abs(b)));
            case 16:return new MoreMathStep(b>0?0:(a>0?1:(output>0?1:0)));
            case 17:return new MoreMathStep(a>0?1:(b>0?0:(output>0?1:0)));
            case 18:
            {
                bool active=a>0;
                double duration=double.IsNaN(b)||b<0?0:b;
                double step=double.IsNaN(dt)||double.IsInfinity(dt)||dt<0?0:dt;
                double nextElapsed=active?(previous>0?elapsed+step:0):0;
                bool nextOutput=active&&nextElapsed>=duration;
                return new MoreMathStep(nextOutput?1:0,active?1:0,nextElapsed);
            }
            case 19:
            {
                bool active=a>0;
                double duration=double.IsNaN(b)||b<0?0:b;
                double step=double.IsNaN(dt)||double.IsInfinity(dt)||dt<0?0:dt;
                double nextElapsed=elapsed;
                bool nextOutput=output>0;
                if(active){nextElapsed=0;nextOutput=true;}
                else if(nextOutput)
                {
                    nextElapsed=previous>0?0:nextElapsed+step;
                    if(nextElapsed>=duration){nextOutput=false;nextElapsed=0;}
                }
                else nextElapsed=0;
                return new MoreMathStep(nextOutput?1:0,active?1:0,nextElapsed);
            }
            case 20:return new MoreMathStep(a>0&&!double.IsInfinity(a)&&b>0&&b!=1&&!double.IsInfinity(b)?Math.Log(a,b):0);
            case 21:return new MoreMathStep(a>0||b>0?1:0);
            case 22:return new MoreMathStep((a>0)^(b>0)?1:0);
            case 23:return new MoreMathStep(a>0&&b>0?0:1);
            case 24:return new MoreMathStep(a<=0&&b<=0?1:0);
            case 25:return new MoreMathStep(a!=b?1:0);
            case 26:return new MoreMathStep(Math.Sqrt(a*a+b*b));
            case 27:return new MoreMathStep(Math.Abs(a)<Math.Abs(b)?0:a);
            case 28:return new MoreMathStep(a>b?1:(a<0-Math.Abs(b)?0:(output>0?1:0)));
            case 29:return new MoreMathStep(b>0?0:(a>0?output+dt:output));
            case 30:
            {
                int direction=a>0?1:(a<0?-1:0);
                double nextOutput=b>0?0:(direction!=0&&direction!=previous?output+direction:output);
                return new MoreMathStep(nextOutput,direction);
            }
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }

    internal static MoreMathStep Unary(int operation,double input,double output,double previous,double dt)
    {
        switch(operation)
        {
            case 10:return new MoreMathStep(Math.Floor(input));
            case 11:return new MoreMathStep(input>0&&previous<=0?(output>0?0:1):(output>0?1:0),input);
            case 12:return new MoreMathStep(input);
            case 13:return new MoreMathStep(Math.Ceiling(input));
            case 14:return new MoreMathStep(input>0?0:1);
            case 15:return new MoreMathStep(Math.Sign(input));
            case 16:return new MoreMathStep(output+input*dt);
            case 17:return new MoreMathStep(Math.Exp(input));
            default:throw new FunctionFault("UNSUPPORTED OPERATION");
        }
    }
}
