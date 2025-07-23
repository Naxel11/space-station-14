using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;      // <-- Убедитесь, что этот using на месте
using System.Threading.Tasks;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Systems;
using JetBrains.Annotations;
using Robust.Shared.Collections;
using Robust.Shared.Map.Components;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Radiation.Systems
{
    public partial class RadiationSystem
    {
        private readonly record struct SourceData(
            float Intensity,
            Entity<RadiationSourceComponent, TransformComponent> Entity,
            Vector2 WorldPosition)
        {
            public EntityUid? GridUid => Entity.Comp2.GridUid;
            public float Slope => Entity.Comp1.Slope;
            public TransformComponent Transform => Entity.Comp2;
        }

        private void UpdateGridcast()
        {
            var debug = _debugSessions.Count > 0;
            var stopwatch = new Robust.Shared.Timing.Stopwatch();
            stopwatch.Start();

            _sources.Clear();
            _sources.EnsureCapacity(Count<RadiationSourceComponent>());
            var sourcesQuery = EntityQueryEnumerator<RadiationSourceComponent, TransformComponent>();
            while (sourcesQuery.MoveNext(out var uid, out var source, out var xform))
            {
                if (!source.Enabled)
                    continue;

                var worldPos = _transform.GetWorldPosition(xform);
                var intensity = source.Intensity * _stack.GetCount(uid);
                intensity = GetAdjustedRadiationIntensity(uid, intensity);
                _sources.Add(new SourceData(intensity, (uid, source, xform), worldPos));
            }

            var debugRays = debug ? new ConcurrentBag<DebugRadiationRay>() : null;
            var receiversTotalRads = new ValueList<(Entity<RadiationReceiverComponent>, float)>();
            var destinationsQuery = EntityQueryEnumerator<RadiationReceiverComponent, TransformComponent>();

            while (destinationsQuery.MoveNext(out var destUid, out var dest, out var destTrs))
            {
                var destWorld = _transform.GetWorldPosition(destTrs);
                var totalRads = 0.0; // Используем double

                Parallel.ForEach<SourceData, double>(
                    _sources,
                    () => 0.0,
                    (source, loopState, localRads) =>
                    {
                        var gridList = new List<Entity<MapGridComponent>>();
                        if (Irradiate(source, destUid, destTrs, destWorld, debug, gridList) is { } ray && ray.ReachedDestination)
                        {
                            localRads += ray.Rads;

                            if (debug)
                            {
                                debugRays!.Add(new DebugRadiationRay(
                                    ray.MapId,
                                    GetNetEntity(ray.SourceUid),
                                    ray.Source,
                                    GetNetEntity(ray.DestinationUid),
                                    ray.Destination,
                                    ray.Rads,
                                    ray.Blockers ?? new())
                                );
                            }
                        }
                        return localRads;
                    },
                    (localRads) =>
                    {
                        // *** ИСПРАВЛЕНИЕ ЗДЕСЬ ***
                        // Потокобезопасное сложение для double через Compare-and-Swap цикл.
                        double initialValue, computedValue;
                        do
                        {
                            initialValue = totalRads;
                            computedValue = initialValue + localRads;
                        }
                        while (initialValue != Interlocked.CompareExchange(ref totalRads, computedValue, initialValue));
                    }
                );

                var finalRads = (float)totalRads;
                finalRads = GetAdjustedRadiationIntensity(destUid, finalRads);
                receiversTotalRads.Add(((destUid, dest), finalRads));
            }

            UpdateGridcastDebugOverlay(stopwatch.Elapsed.TotalMilliseconds, _sources.Count, receiversTotalRads.Count, debugRays?.ToList());

            foreach (var (receiver, rads) in receiversTotalRads)
            {
                if (Deleted(receiver))
                    continue;

                receiver.Comp.CurrentRadiation = rads;
                if (rads > 0)
                    IrradiateEntity(receiver, rads, GridcastUpdateRate);
            }

            RaiseLocalEvent(new RadiationSystemUpdatedEvent());
        }

        // ... (остальные методы Irradiate, Gridcast, GetAdjustedRadiationIntensity без изменений) ...

        private RadiationRay? Irradiate(SourceData source,
            EntityUid destUid,
            TransformComponent destTrs,
            Vector2 destWorld,
            bool saveVisitedTiles,
            List<Entity<MapGridComponent>> gridList)
        {
            if (source.Transform.MapID != destTrs.MapID)
                return null;

            var mapId = destTrs.MapID;
            var dir = destWorld - source.WorldPosition;
            var dist = dir.Length();

            if (dist > GridcastMaxDistance)
                return null;

            var rads = source.Intensity - source.Slope * dist;
            if (rads < MinIntensity)
                return null;

            var ray = new RadiationRay(mapId, source.Entity, source.WorldPosition, destUid, destWorld, rads);

            var box = Box2.FromTwoPoints(source.WorldPosition, destWorld);
            gridList.Clear();
            _mapManager.FindGridsIntersecting(mapId, box, ref gridList, true);

            foreach (var grid in gridList)
            {
                ray = Gridcast((grid.Owner, grid.Comp, Transform(grid)), ref ray, saveVisitedTiles, source.Transform, destTrs);
                if (ray.Rads <= 0)
                    return ray;
            }

            return ray;
        }

        private RadiationRay Gridcast(
            Entity<MapGridComponent, TransformComponent> grid,
            ref RadiationRay ray,
            bool saveVisitedTiles,
            TransformComponent sourceTrs,
            TransformComponent destTrs)
        {
            var blockers = saveVisitedTiles ? new List<(Vector2i, float)>() : null;
            var gridUid = grid.Owner;
            if (!_resistanceQuery.TryGetComponent(gridUid, out var resistance))
                return ray;

            var resistanceMap = resistance.ResistancePerTile;

            Vector2 srcLocal = sourceTrs.ParentUid == grid.Owner
                ? sourceTrs.LocalPosition
                : Vector2.Transform(ray.Source, grid.Comp2.InvLocalMatrix);

            Vector2 dstLocal = destTrs.ParentUid == grid.Owner
                ? destTrs.LocalPosition
                : Vector2.Transform(ray.Destination, grid.Comp2.InvLocalMatrix);

        Vector2i sourceGrid = new(
            (int)Math.Floor(srcLocal.X / grid.Comp1.TileSize),
            (int)Math.Floor(srcLocal.Y / grid.Comp1.TileSize));

        Vector2i destGrid = new(
            (int)Math.Floor(dstLocal.X / grid.Comp1.TileSize),
            (int)Math.Floor(dstLocal.Y / grid.Comp1.TileSize));

            var line = new GridLineEnumerator(sourceGrid, destGrid);
            while (line.MoveNext())
            {
                var point = line.Current;
                if (!resistanceMap.TryGetValue(point, out var resData))
                    continue;

                ray.Rads -= resData;
                if (saveVisitedTiles)
                    blockers!.Add((point, ray.Rads));

                if (ray.Rads <= MinIntensity)
                {
                    ray.Rads = 0;
                    break;
                }
            }

            if (!saveVisitedTiles || blockers!.Count <= 0)
                return ray;

            ray.Blockers ??= new();
            ray.Blockers.Add(GetNetEntity(gridUid), blockers);

            return ray;
        }

        private float GetAdjustedRadiationIntensity(EntityUid uid, float rads)
        {
            var child = uid;
            var xform = Transform(uid);
            var parent = xform.ParentUid;

            while (parent.IsValid())
            {
                var parentXform = Transform(parent);
                var childMeta = MetaData(child);

                if ((childMeta.Flags & MetaDataFlags.InContainer) != MetaDataFlags.InContainer)
                {
                    child = parent;
                    parent = parentXform.ParentUid;
                    continue;
                }

                if (_blockerQuery.TryComp(xform.ParentUid, out var blocker))
                {
                    rads -= blocker.RadResistance;
                    if (rads < 0)
                        return 0;
                }

                child = parent;
                parent = parentXform.ParentUid;
            }

            return rads;
        }
    }
}
