using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// PartSettings field population and building-cost recipes. Visual, socket,
// bounds and interaction construction live in the sibling build stages.
internal static class PartBuild
{
    private static readonly SnappingStyle[] HullOnlySnapping =
    {
        SnappingStyle.Floor,
        SnappingStyle.WallPivotable,
        SnappingStyle.WallSquared,
        SnappingStyle.SlopedWall,
        SnappingStyle.SlopedCorner,
        SnappingStyle.SlopedCornerInv,
    };

    // Order matters and mirrors the legacy builder: declared settings first,
    // then visuals/meshes/sockets, then measured bounds, interaction fit and
    // the hover name. Socket seating runs afterwards in SocketSeating.Apply.
    public static void Configure(PartSpec spec, PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = spec.Label;
        settings.description = spec.Description;
        settings.localizedDescription = null;
        settings.internalStateClass = spec.StateClass;
        settings.customCreationClass = null;
        settings.mass = Mathf.Clamp(spec.Mass, 0.1f, 2000f);
        if (spec.SnappingDeclared)
            settings.snappingStyle = ResolveSnapping(spec, settings);
        settings.size = spec.BoundsSize;
        SetCost(spec, settings);

        Transform visual = PartVisuals.VisualRoot(spec, prefab);
        if (spec.ClearDonorVisual)
            PartVisuals.ClearDonorVisual(spec, visual);
        if (spec.Meshes != null && spec.Meshes.Count > 0)
        {
            Transform meshes = PartVisuals.MeshRoot(spec, visual);
            foreach (MeshSpec mesh in spec.Meshes)
                PartVisuals.MeshChild(spec, meshes, mesh);
        }
        if (spec.SocketsDeclared)
            PartSockets.Build(spec, prefab);

        // Bounds auto-measure runs after sockets so socket facing keeps the
        // declared centre, matching the legacy ordering exactly.
        Vector3 boundsCenter = spec.BoundsCenter;
        Vector3 boundsSize = spec.BoundsSize;
        if (spec.BoundsAuto && PartVisuals.MeasureBounds(spec, prefab, out boundsCenter, out boundsSize))
        {
            spec.BoundsCenter = boundsCenter;
            spec.BoundsSize = boundsSize;
        }

        var bounds = prefab.GetComponent<ShipPartBounds>() ?? prefab.AddComponent<ShipPartBounds>();
        bounds.center = boundsCenter;
        bounds.bounds = boundsSize;
        if (spec.FitInteraction)
            PartVisuals.FitInteraction(spec, prefab, boundsCenter, boundsSize);
        PartVisuals.NameForHover(spec, prefab);
        NativeServices.Trace(
            spec.Asset,
            "built from donor "
                + spec.Donor
                + " with "
                + (spec.Meshes?.Count ?? 0)
                + " meshes and "
                + (spec.Sockets?.Count ?? 0)
                + " sockets"
        );
    }

    private static SnappingStyle ResolveSnapping(PartSpec spec, PartSettings settings)
    {
        // Hull-only snapping styles crash the build tool preview on floating parts.
        if (settings is HullPartSettings || Array.IndexOf(HullOnlySnapping, spec.Snapping) < 0)
            return spec.Snapping;
        NativeServices.Error(
            spec.Asset,
            "snapping style " + spec.Snapping + " only works on hull parts, using PreciseOnFloor instead"
        );
        return SnappingStyle.PreciseOnFloor;
    }

    // buildingCost lives on PartSettings as a List<ItemInstance>; Items is not
    // a compile reference, so both the settings lookup and instance creation go
    // through ItemCatalog's reflection bridge.
    private static void SetCost(PartSpec spec, PartSettings settings)
    {
        if (spec.Cost == null || spec.Cost.Count == 0)
            return;
        try
        {
            var listType = typeof(List<>).MakeGenericType(ItemCatalog.ItemInstanceType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var pair in spec.Cost)
            {
                object item = ItemCatalog.Find(pair.Key);
                if (item == null)
                {
                    NativeServices.Warn(spec.Asset, "cost item " + pair.Key + " not found, dropped from the recipe");
                    continue;
                }
                list.Add(Activator.CreateInstance(ItemCatalog.ItemInstanceType, item, pair.Value));
            }
            AccessTools.Field(typeof(PartSettings), "buildingCost").SetValue(settings, list);
        }
        catch (Exception error)
        {
            NativeServices.Warn(spec.Asset, "could not set building cost :: " + error.Message);
        }
    }
}
