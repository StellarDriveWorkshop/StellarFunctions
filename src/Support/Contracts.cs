using System;
using System.Collections.Generic;
using Ships.Interface.Model;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// Support-owned input contracts. Functions maps its content definitions
// (functions.json via the existing pack parser) into these specs; support
// services consume them without knowing the pack format.

public sealed class SocketSpec
{
    public byte Id;
    public string Type = "item";
    public Vector3 Position;
    public Vector3? Facing;
    public bool Relayed;
}

public sealed class MeshSpec
{
    public string Shape;
    public string DonorPath;
    public string Material;
    public Vector3 Position;
    public Vector3 Euler;
    public Vector3 Scale = Vector3.one;
    public Color? Tint;
    public string Text;
    public string Screen;
    public string Font;
    public Color? TextColor;
    public int Columns = 16;
    public float Speed = 6f;
}

public sealed class PartSpec
{
    public ushort Id;
    public string Asset;
    public string Label;
    public string Description;
    public string Donor;
    public string StateClass;
    public float Mass;
    public SnappingStyle Snapping;
    public bool SnappingDeclared;
    public Vector3 BoundsCenter;
    public Vector3 BoundsSize;
    public bool ClearDonorVisual;
    public bool BoundsAuto;
    public bool SocketsDeclared;
    public bool FitInteraction;
    public string Menu;
    public bool Attached;
    public string BuildMenuAfter;
    public IReadOnlyDictionary<uint, int> Cost;
    public IReadOnlyList<MeshSpec> Meshes;
    public IReadOnlyList<SocketSpec> Sockets;
}
