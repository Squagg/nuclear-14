// #Misfits Change - Willower Tree delivery, cooldown, and default regression coverage.
using System.Linq;
using Content.Server._Misfits.SmokeSignal;
using Content.Shared._Misfits.SmokeSignal;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Misfits;

[TestFixture]
public sealed class WillowerTreeCommunicationsTest
{
    [Test]
    public async Task TreeAnnouncementUsesTreeConfigAndKeepsDefaultSignalSettings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var tree = entities.SpawnEntity("TribalTree", map.GridCoords);
            var treeSignal = entities.GetComponent<SmokeSignalComponent>(tree);
            var signalFire = entities.SpawnEntity("MisfitsTribalSignalFire", map.GridCoords);
            var defaultSignal = entities.GetComponent<SmokeSignalComponent>(signalFire);

            Assert.Multiple(() =>
            {
                Assert.That(treeSignal.Cooldown, Is.EqualTo(TimeSpan.FromSeconds(300)));
                Assert.That(treeSignal.MaxMessageLength, Is.EqualTo(128));
                Assert.That(treeSignal.TargetDepartment, Is.EqualTo("Tribe"));
                Assert.That(treeSignal.NearbyRange, Is.Zero);
                Assert.That(treeSignal.OpenOnActivate, Is.False);
                Assert.That(treeSignal.ActivatorJobs, Is.EquivalentTo(["TribalShaman", "TribalElder"]));
                Assert.That(treeSignal.Verb, Is.EqualTo("willower-tree-announce-verb"));
                Assert.That(treeSignal.BroadcastMessage, Is.EqualTo("willower-tree-announcement"));
                Assert.That(treeSignal.CooldownMessage, Is.EqualTo("willower-tree-announcement-cooldown"));
                Assert.That(defaultSignal.ActivatorJobs, Is.Null);
                Assert.That(defaultSignal.OpenOnActivate, Is.True);
                Assert.That(defaultSignal.Verb, Is.EqualTo("smoke-signal-verb"));
                Assert.That(defaultSignal.BroadcastMessage, Is.EqualTo("smoke-signal-broadcast"));
                Assert.That(defaultSignal.CooldownMessage, Is.EqualTo("smoke-signal-cooldown"));
                Assert.That(defaultSignal.NearbyRange, Is.EqualTo(18f));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TreeAnnouncementDeliversToLivingWillowersAndSharesCooldown()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
            DummyTicker = false,
        });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entities = server.ResolveDependency<IEntityManager>();
        var minds = entities.System<SharedMindSystem>();
        var roles = entities.System<SharedRoleSystem>();
        var signals = entities.System<SmokeSignalSystem>();
        var playerManager = server.ResolveDependency<IPlayerManager>();
        var timing = server.ResolveDependency<IGameTiming>();

        await server.WaitAssertion(() =>
        {
            EntityUid SpawnWithJob(string job, bool dead = false)
            {
                var body = entities.SpawnEntity(null, map.GridCoords);
                entities.EnsureComponent<MindContainerComponent>(body);
                var mind = minds.CreateMind(null).Owner;
                minds.TransferTo(mind, body);
                roles.MindAddRole(mind, new JobComponent { Prototype = job });
                entities.EnsureComponent<ActorComponent>(body);
                if (dead)
                    entities.EnsureComponent<MobStateComponent>(body).CurrentState = MobState.Dead;
                return body;
            }

            var shaman = playerManager.Sessions.Single().AttachedEntity!.Value;
            Assert.That(minds.TryGetMind(shaman, out var shamanMind, out _), Is.True);
            roles.MindAddRole(shamanMind, new JobComponent { Prototype = "TribalShaman" });

            var elder = SpawnWithJob("TribalElder");
            var tribal = SpawnWithJob("Tribal");
            var superMutant = SpawnWithJob("SuperMutantTribal");
            var protectron = SpawnWithJob("SyntheticProtectronTribal");
            var outsider = SpawnWithJob("Wastelander");
            var deadTribal = SpawnWithJob("Tribal", dead: true);
            var tree = entities.SpawnEntity("TribalTree", map.GridCoords);
            var component = entities.GetComponent<SmokeSignalComponent>(tree);

            Assert.Multiple(() =>
            {
                Assert.That(signals.CanUse(shaman, component), Is.True);
                Assert.That(signals.CanUse(elder, component), Is.True);
                Assert.That(signals.CanUse(tribal, component), Is.False);
                Assert.That(signals.GetRecipients(component.TargetDepartment),
                    Is.EquivalentTo(new[] { shaman, elder, tribal, superMutant, protectron }));
            });

            entities.RemoveComponent<ActorComponent>(elder);
            entities.RemoveComponent<ActorComponent>(tribal);
            entities.RemoveComponent<ActorComponent>(superMutant);
            entities.RemoveComponent<ActorComponent>(protectron);
            entities.RemoveComponent<ActorComponent>(outsider);
            entities.RemoveComponent<ActorComponent>(deadTribal);

            var longMessage = new SmokeSignalSendMessage(new string('x', 129)) { Actor = shaman };
            entities.EventBus.RaiseComponentEvent(tree, component, longMessage);
            var cooldownEnd = component.CooldownEnd;

            Assert.That(cooldownEnd, Is.EqualTo(timing.CurTime + TimeSpan.FromSeconds(300)));

            entities.EventBus.RaiseComponentEvent(tree, component, new SmokeSignalSendMessage("second") { Actor = elder });
            Assert.That(component.CooldownEnd, Is.EqualTo(cooldownEnd));
        });

        await pair.CleanReturnAsync();
    }
}
