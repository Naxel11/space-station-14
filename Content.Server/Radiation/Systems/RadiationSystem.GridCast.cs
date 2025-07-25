using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Systems;
using JetBrains.Annotations;
using Robust.Shared.Collections;
using Robust.Shared.Map.Components;
using Robust.Shared.Threading;
using Robust.Shared.Utility;

namespace Content.Server.Radiation.Systems
{
    // main algorithm that fire radiation rays to target
    public partial class RadiationSystem
    {
        private readonly record struct SourceData(
            float Intensity,
            float Slope,
            float MaxRange,
            Entity<RadiationSourceComponent, TransformComponent> Entity)
        {
            public EntityUid Uid => Entity.Owner;
            public TransformComponent Transform => Entity.Comp2;
        }

        private void UpdateGridcast()
        {
            var debug = _debugSessions.Count > 0;
            var stopwatch = new Robust.Shared.Timing.Stopwatch();
            stopwatch.Start();

            // --- НАЧАЛО ИЗМЕНЕНИЙ: Добавлены таймеры ---
            var stopwatch2 = new Robust.Shared.Timing.Stopwatch();
            stopwatch2.Start();

            // 1. Подготовка данных источников из постоянной коллекции
            var sources = new List<SourceData>(_activeSources.Count);
            
            foreach (var (uid, (source, xform)) in _activeSources)
            {
                if (!source.Enabled)
                    continue;

                var intensity = source.Intensity * _stack.GetCount(uid);
                intensity = GetAdjustedRadiationIntensity(uid, intensity);

                var maxRange = source.Slope > 1e-6f ? intensity / source.Slope : float.MaxValue;
                sources.Add(new SourceData(intensity, source.Slope, maxRange, (uid, source, xform)));
            }
            var timer1 = stopwatch2.Elapsed;
            stopwatch2.Restart();

            // 2. Подготовка данных приемников из постоянной коллекции
            var destinations = new ValueList<(EntityUid Uid, TransformComponent Xform)>(_activeReceivers);
            var timer2 = stopwatch2.Elapsed;
            stopwatch2.Restart();

            if (destinations.Count == 0 || sources.Count == 0)
            {
                UpdateGridcastDebugOverlay(stopwatch.Elapsed.TotalMilliseconds, sources.Count, destinations.Count, null);
                RaiseLocalEvent(new RadiationSystemUpdatedEvent());
                return;
            }
            var timer3 = stopwatch2.Elapsed;
            stopwatch2.Restart();

            var results = new float[destinations.Count];
            var debugRays = debug ? new ConcurrentBag<DebugRadiationRay>() : null;

            var job = new RadiationJob
            {
                System = this,
                Sources = sources,
                Destinations = destinations,
                Results = results,
                DebugRays = debugRays,
                Debug = debug
            };

            // 4. Выполнение параллельной задачи
            _parallel.ProcessNow(job, destinations.Count);
            var timer4 = stopwatch2.Elapsed;
            stopwatch2.Restart();

            // 5. Применение результатов
            for (var i = 0; i < destinations.Count; i++)
            {
                var (uid, _) = destinations[i];
                var rads = results[i];

                if (Deleted(uid) || !TryComp<RadiationReceiverComponent>(uid, out var receiver))
                    continue;

                receiver.CurrentRadiation = rads;
                if (rads > 0)
                    IrradiateEntity(uid, rads, GridcastUpdateRate);
            }
            var timer5 = stopwatch2.Elapsed;
            stopwatch2.Restart();

            UpdateGridcastDebugOverlay(stopwatch.Elapsed.TotalMilliseconds, sources.Count, destinations.Count, debugRays?.ToList());
            RaiseLocalEvent(new RadiationSystemUpdatedEvent());

            Logger.Info(timer1.TotalMilliseconds + " " + timer2.TotalMilliseconds + " " + timer3.TotalMilliseconds + " " + timer4.TotalMilliseconds + " " + timer5.TotalMilliseconds);
            // --- КОНЕЦ ИЗМЕНЕНИЙ ---
        }

        private RadiationRay? Irradiate(SourceData source,
            EntityUid destUid,
            TransformComponent destTrs,
            Vector2 destWorld,
            bool saveVisitedTiles,
            List<Entity<MapGridComponent>> gridList)
        {
            var mapId = destTrs.MapID;
            var sourceWorldPos = _transform.GetWorldPosition(source.Transform);
            var dist = (destWorld - sourceWorldPos).Length();

            if (dist > source.MaxRange)
                return null;

            var rads = source.Intensity - source.Slope * dist;

            if (rads < MinIntensity)
                return null;

            var ray = new RadiationRay(mapId, source.Entity, sourceWorldPos, destUid, destWorld, rads);
            var box = Box2.FromTwoPoints(sourceWorldPos, destWorld);
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

        [UsedImplicitly]
        private readonly record struct RadiationJob : IParallelRobustJob
        {
            public int BatchSize => 5;

            public required RadiationSystem System { get; init; }
            public required List<SourceData> Sources { get; init; }
            public required ValueList<(EntityUid Uid, TransformComponent Xform)> Destinations { get; init; }
            public required float[] Results { get; init; }
            public required ConcurrentBag<DebugRadiationRay>? DebugRays { get; init; }
            public required bool Debug { get; init; }

            public void Execute(int index)
            {
                var (destUid, destTrs) = Destinations[index];
                var gridList = new List<Entity<MapGridComponent>>();
                var destWorld = System._transform.GetWorldPosition(destTrs);
                var rads = 0f;
                var destMapId = destTrs.MapID;

                foreach (var source in Sources)
                {
                    if (source.Transform.MapID != destMapId)
                        continue;

                    var sourceWorldPos = System._transform.GetWorldPosition(source.Transform);

                    var delta = sourceWorldPos - destWorld;
                    if (delta.LengthSquared() > source.MaxRange * source.MaxRange)
                        continue;

                    if (System.Irradiate(source, destUid, destTrs, destWorld, Debug, gridList) is not { } ray)
                        continue;

                    if (ray.ReachedDestination)
                        rads += ray.Rads;

                    if (Debug)
                    {
                        DebugRays!.Add(new DebugRadiationRay(
                            ray.MapId,
                            System.GetNetEntity(ray.SourceUid),
                            ray.Source,
                            System.GetNetEntity(ray.DestinationUid),
                            ray.Destination,
                            ray.Rads,
                            ray.Blockers ?? new())
                        );
                    }
                }

                rads = System.GetAdjustedRadiationIntensity(destUid, rads);
                Results[index] = rads;
            }
        }
    }
}
