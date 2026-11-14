// Check the actual loaded artifact, not just source/build manifest agreement.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace StellarFunctions;
internal static class ReleaseMetadataChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var assembly=typeof(FunctionReturnState).Assembly;
        var info=CustomAttributeData.GetCustomAttributes(assembly).Single(a=>a.AttributeType.Name=="MelonInfoAttribute");
        string version=(string)info.ConstructorArguments[2].Value;
        using var resource=assembly.GetManifestResourceStream("content.functions.json");
        check(resource!=null,"release contains its embedded content manifest");
        using var reader=new StreamReader(resource);
        var content=JObject.Parse(reader.ReadToEnd());
        check((string)content["id"]=="stellar-functions","release manifest belongs to Functions");
        check((string)content["version"]==version,"packaged content version matches MelonInfo (content="+
            (string)content["version"]+", mod="+version+")");
    }
}
