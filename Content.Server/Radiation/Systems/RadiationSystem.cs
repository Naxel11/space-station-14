using Content.Server.Radiation.Components;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Events;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Threading;

namespace Content.Server.Radiation.Systems;

public sealed partial class RadiationSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;

    private EntityQuery<RadiationBlockingContainerComponent> _blockerQuery;
    private EntityQuery<RadiationGridResistanceComponent> _resistanceQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<StackComponent> _stackQuery;

    // --- НАЧАЛО ИЗМЕНЕНИЙ: Постоянные коллекции для источников и приемников ---
    private readonly Dictionary<EntityUid, (RadiationSourceComponent Source, TransformComponent Xform)> _activeSources = new();
    private readonly List<(EntityUid Uid, TransformComponent Xform)> _activeReceivers = new();
    // --- КОНЕЦ ИЗМЕНЕНИЙ ---

    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeCvars();
        InitRadBlocking();

        _blockerQuery = GetEntityQuery<RadiationBlockingContainerComponent>();
        _resistanceQuery = GetEntityQuery<RadiationGridResistanceComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();

        // --- НАЧАЛО ИЗМЕНЕНИЙ: Подписки на события для обоих типов компонентов ---
        SubscribeLocalEvent<RadiationSourceComponent, ComponentInit>(OnSourceInit);
        SubscribeLocalEvent<RadiationSourceComponent, ComponentShutdown>(OnSourceShutdown);

        SubscribeLocalEvent<RadiationReceiverComponent, ComponentInit>(OnReceiverInit);
        SubscribeLocalEvent<RadiationReceiverComponent, ComponentShutdown>(OnReceiverShutdown);
        // --- КОНЕЦ ИЗМЕНЕНИЙ ---
    }

    // --- НАЧАЛО ИЗМЕНЕНИЙ: Методы-обработчики событий ---
    private void OnSourceInit(EntityUid uid, RadiationSourceComponent component, ComponentInit args)
    {
        if (TryComp<TransformComponent>(uid, out var xform))
        {
            _activeSources[uid] = (component, xform);
        }
    }

    private void OnSourceShutdown(EntityUid uid, RadiationSourceComponent component, ComponentShutdown args)
    {
        _activeSources.Remove(uid);
    }

    private void OnReceiverInit(EntityUid uid, RadiationReceiverComponent component, ComponentInit args)
    {
        if (TryComp<TransformComponent>(uid, out var xform))
        {
            _activeReceivers.Add((uid, xform));
        }
    }

    private void OnReceiverShutdown(EntityUid uid, RadiationReceiverComponent component, ComponentShutdown args)
    {
        for (var i = _activeReceivers.Count - 1; i >= 0; i--)
        {
            if (_activeReceivers[i].Uid == uid)
            {
                _activeReceivers.RemoveAt(i);
                return;
            }
        }
    }
    // --- КОНЕЦ ИЗМЕНЕНИЙ ---

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < GridcastUpdateRate)
            return;

        UpdateGridcast();
        UpdateResistanceDebugOverlay();
        _accumulator = 0f;
    }

    public void IrradiateEntity(EntityUid uid, float radsPerSecond, float time)
    {
        var msg = new OnIrradiatedEvent(time, radsPerSecond, uid);
        RaiseLocalEvent(uid, msg);
    }

    public void SetSourceEnabled(Entity<RadiationSourceComponent?> entity, bool val)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        // Это изменение подхватится в UpdateGridcast при проверке `source.Enabled`
        entity.Comp.Enabled = val;
    }

    public void SetCanReceive(EntityUid uid, bool canReceive)
    {
        if (canReceive)
        {
            EnsureComp<RadiationReceiverComponent>(uid);
        }
        else
        {
            RemComp<RadiationReceiverComponent>(uid);
        }
    }
}
