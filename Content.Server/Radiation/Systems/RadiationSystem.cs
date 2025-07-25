using Content.Server.Radiation.Components;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Events;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
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

    private readonly DynamicTree<EntityUid> _sourceTree = new(static (in EntityUid _) => default, EqualityComparer<EntityUid>.Default);
    private readonly Dictionary<EntityUid, SourceData> _sourceDataMap = new();
    private readonly List<EntityUid> _activeReceivers = new();

    private float _accumulator;
    private List<SourceData> _sources = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeCvars();
        InitRadBlocking();

        // Подписки на события для ИСТОЧНИКОВ
        SubscribeLocalEvent<RadiationSourceComponent, ComponentInit>(OnSourceInit);
        SubscribeLocalEvent<RadiationSourceComponent, ComponentShutdown>(OnSourceShutdown);
        SubscribeLocalEvent<RadiationSourceComponent, MoveEvent>(OnSourceMove);
        SubscribeLocalEvent<RadiationSourceComponent, StackCountChangedEvent>(OnSourceStackChanged);

        // Подписки на события для ПРИЕМНИКОВ
        SubscribeLocalEvent<RadiationReceiverComponent, ComponentInit>(OnReceiverInit);
        SubscribeLocalEvent<RadiationReceiverComponent, ComponentShutdown>(OnReceiverShutdown);

        _blockerQuery = GetEntityQuery<RadiationBlockingContainerComponent>();
        _resistanceQuery = GetEntityQuery<RadiationGridResistanceComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();
    }

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

        entity.Comp.Enabled = val;
    }

    /// <summary>
    ///     Marks entity to receive/ignore radiation rays.
    /// </summary>
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
