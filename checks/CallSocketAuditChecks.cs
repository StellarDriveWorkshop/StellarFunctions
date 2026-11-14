// Read-only socket audit for the one-input Call / Signal Display report
// (StellarFunctions issue #1). Enumerates every Call geometry variant's
// sockets and every native signal socket, then reports sub-5 cm coincidences
// across yaw rotations. Diagnostic only: it changes nothing and asserts only
// that enumeration succeeded. Results go to Debug.Log (unity.log) plus a
// check() summary.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions;

internal static class CallSocketAuditChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var asm = typeof(FunctionCallState).Assembly;
        var sizing = asm.GetType("StellarFunctions.CallSizing");
        var variant = AccessTools.Method(sizing, "Variant");
        check(variant != null, "socket audit resolves CallSizing.Variant");

        // Same placement frame the native fixtures use; socket positions are
        // compared in this frame's local space.
        var frame = new FloatingPartPlacement(Vector3.zero, PartOrientation.Forward, PartOrientation.Up);
        Quaternion frameRot = frame.Rotation;

        // Socket positions in placement space: native parts use the socket's
        // prefab position (what native GetSocketPosition returns); Functions
        // sockets use the marker-root local position (what
        // FunctionSocketPositionFix returns).
        Vector3 SocketPos(PartSettings p, SocketObject s)
        {
            var marker = s.GetComponent<FunctionSocketMarker>();
            if (marker != null && marker.Root != null)
                return marker.Root.InverseTransformPoint(s.transform.position);
            return s.transform.position;
        }

        var parts = new List<PartSettings>();
        foreach (var p in Resources.FindObjectsOfTypeAll<PartSettings>())
        {
            if (p == null || p.part == null)
                continue;
            var sockets = p.part.GetComponentsInChildren<SocketObject>(true);
            if (sockets == null || !sockets.Any(s => s != null && s.Type == CableType.Signal))
                continue;
            parts.Add(p);
        }
        check(parts.Count > 0, "socket audit enumerated " + parts.Count + " signal-capable part settings");

        var rows = new List<string>();
        foreach (int count in new[] { 1, 2, 3 })
        {
            var call = (PartSettings)variant.Invoke(null, new object[] { count });
            var callSockets = call
                .part.GetComponentsInChildren<SocketObject>(true)
                .Where(s => s != null && s.Type == CableType.Signal)
                .Select(s => new { s.Id, Pos = frameRot * SocketPos(call, s) })
                .ToArray();
            foreach (var neighbor in parts)
            {
                var nearSockets = neighbor
                    .part.GetComponentsInChildren<SocketObject>(true)
                    .Where(s => s != null && s.Type == CableType.Signal)
                    .ToArray();
                if (nearSockets.Length == 0)
                    continue;
                foreach (var cs in callSockets)
                foreach (var ns in nearSockets)
                {
                    Vector3 np = frameRot * SocketPos(neighbor, ns);
                    foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
                    {
                        Vector3 yawed = Quaternion.AngleAxis(yaw, Vector3.up) * np;
                        Vector3 offset = cs.Pos - yawed;
                        float horizontal = Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
                        if (Mathf.Abs(offset.y) >= 0.05f || horizontal > 1.0f)
                            continue;
                        rows.Add(
                            $"call{count} socket{cs.Id} ~ {neighbor.name}#{neighbor.id} socket{ns.Id} yaw{yaw:0} offset=({offset.x:F3},{offset.y:F3},{offset.z:F3})"
                        );
                    }
                }
            }
        }
        var ordered = rows.OrderBy(r => r).ToArray();
        var auditDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "menu-probe-output");
        Directory.CreateDirectory(auditDir);
        File.WriteAllLines(Path.Combine(auditDir, "socket-audit.txt"), ordered);
        foreach (var row in ordered.Take(80))
            Debug.Log("[socket-audit] " + row);
        if (ordered.Length > 80)
            Debug.Log(
                "[socket-audit] ... "
                    + (ordered.Length - 80)
                    + " more coincidences (full list in menu-probe-output/socket-audit.txt)"
            );
        check(true, "socket audit complete: " + ordered.Length + " sub-5cm socket coincidences across Call sizes 1-3");
    }
}
