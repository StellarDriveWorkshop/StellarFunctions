using System;
using System.Collections.Generic;
using System.Linq;
using Ships.Interface.Settings;
using UnityEngine;

namespace StellarFunctions.Support;

// Ordered registration coordinator. Owns the spec list, materialization
// passes, failure bookkeeping and the built-settings registry that
// Catalogue and MenuCatalog consume. Materialization is lazy and retried:
// a spec whose donor asset is not yet available stays pending; a spec that
// fails to build or collides with a foreign part id is failed permanently
// for the session, matching the previous pipeline's semantics.
public static class Registration
{
    private const string Scope = "registration";
    private static readonly List<PartSpec> Specs = new List<PartSpec>();
    private static readonly Dictionary<ushort, PartSettings> Built = new Dictionary<ushort, PartSettings>();
    private static readonly HashSet<ushort> Failed = new HashSet<ushort>();
    private static bool _building;
    private static bool _readyNotified;
    private static PartSettings[] _sources;

    public static Action PartsReady;

    // Caller-installed one-time bootstrap (catalogue and menu installs).
    // Invoked before registration and materialization so no path can bypass
    // initialization. Returns true only when every required subsystem
    // verified its install; a false return or exception is logged, blocks
    // materialization until a retry succeeds, and never latches success.
    public static Func<bool> Bootstrap;

    // 0 = not yet succeeded, 1 = succeeded, -1 = last attempt failed.
    private static int _bootstrap;

    public static bool BootstrapReady => _bootstrap == 1;

    private static bool EnsureBootstrap()
    {
        if (Bootstrap == null || _bootstrap == 1)
        {
            return _bootstrap == 1 || Bootstrap == null;
        }

        bool ready;
        try
        {
            ready = Bootstrap();
        }
        catch (Exception ex)
        {
            _bootstrap = -1;
            NativeServices.Error(Scope, "bootstrap failed; materialization paused until a retry succeeds", ex);
            return false;
        }
        _bootstrap = ready ? 1 : -1;
        if (!ready)
        {
            NativeServices.Error(Scope, "bootstrap reported not-ready; materialization paused until a retry succeeds");
        }

        return ready;
    }

    public static int SpecCount => Specs.Count;
    public static int BuiltCount => Built.Count;
    public static bool Degraded => Failed.Count > 0;
    public static IEnumerable<ushort> FailedIds => Failed;

    public static void Register(PartSpec spec)
    {
        EnsureBootstrap();
        if (spec == null || !SpecRules.ValidSpec(spec.Id, spec.Asset))
        {
            return;
        }

        if (Specs.Any(s => s.Id == spec.Id))
        {
            NativeServices.Warn(Scope, "duplicate part spec id " + spec.Id + " ignored");
            return;
        }
        Specs.Add(spec);
    }

    public static PartSettings Get(ushort id)
    {
        EnsureMaterialized();
        return Built.TryGetValue(id, out var settings) ? settings : null;
    }

    public static IEnumerable<KeyValuePair<string, PartSettings>> MenuEntries()
    {
        foreach (var spec in Specs)
        {
            if (Failed.Contains(spec.Id))
            {
                continue;
            }

            if (
                Built.TryGetValue(spec.Id, out var settings)
                && settings != null
                && !string.IsNullOrEmpty(spec.BuildMenuAfter)
            )
            {
                yield return new KeyValuePair<string, PartSettings>(spec.BuildMenuAfter, settings);
            }
        }
    }

    public static void EnsureMaterialized()
    {
        // Materialization requires a healthy bootstrap: building parts
        // without screen hooks, catalogue injection or menu registration
        // would be a silently partial initialization. A failed bootstrap
        // is retried on the next call.
        if (!EnsureBootstrap())
        {
            return;
        }

        if (_building)
        {
            return;
        }

        _building = true;
        try
        {
            Pass();
        }
        finally
        {
            _building = false;
        }
    }

    private static void Pass()
    {
        if (Specs.Count == 0 || Built.Count + Failed.Count == Specs.Count)
        {
            return;
        }

        _sources = Resources.FindObjectsOfTypeAll<PartSettings>();
        foreach (var spec in Specs)
        {
            if (Built.ContainsKey(spec.Id) || Failed.Contains(spec.Id))
            {
                continue;
            }

            var donor = FindDonor(spec.Donor);
            if (donor == null)
            {
                continue; // donor asset not available yet; retry next pass
            }

            var foreign = FindForeignId(spec.Id);
            if (foreign != null)
            {
                Failed.Add(spec.Id);
                NativeServices.Error(
                    Scope,
                    "part id "
                        + spec.Id
                        + " already used by foreign settings '"
                        + foreign.name
                        + "'; refusing to register"
                );
                continue;
            }
            try
            {
                var settings = PartFactory.Build(spec, donor);
                Built[spec.Id] = settings;
                Catalogue.Register(settings);
                NativeServices.Trace(Scope, "built part " + spec.Id + " (" + spec.Asset + ")");
            }
            catch (Exception ex)
            {
                Failed.Add(spec.Id);
                NativeServices.Error(Scope, "failed to build part " + spec.Id + " (" + spec.Asset + ")", ex);
            }
        }
        _sources = null;
        // Readiness means every required spec genuinely built. Partial
        // failure is reported separately and does not fire PartsReady.
        var readiness = SpecRules.Evaluate(Built.Count, Failed.Count, Specs.Count);
        if (!_readyNotified && readiness != SpecRules.Readiness.Awaiting)
        {
            _readyNotified = true;
            if (readiness == SpecRules.Readiness.Ready)
            {
                try
                {
                    PartsReady?.Invoke();
                }
                catch (Exception ex)
                {
                    NativeServices.Error(Scope, "PartsReady handler failed", ex);
                }
            }
            else
            {
                NativeServices.Error(
                    Scope,
                    "registration incomplete: "
                        + Built.Count
                        + "/"
                        + Specs.Count
                        + " parts built, "
                        + Failed.Count
                        + " failed; PartsReady not fired"
                );
            }
        }
    }

    private static PartSettings FindDonor(string donorName)
    {
        if (string.IsNullOrEmpty(donorName) || _sources == null)
        {
            return null;
        }

        foreach (var p in _sources)
        {
            if (p != null && p.name == donorName && p.part != null && !Built.Values.Contains(p))
            {
                return p;
            }
        }

        return null;
    }

    private static PartSettings FindForeignId(ushort id)
    {
        if (_sources == null)
        {
            return null;
        }

        foreach (var p in _sources)
        {
            if (p != null && p.id == id && !Built.Values.Contains(p))
            {
                return p;
            }
        }

        return null;
    }
}
