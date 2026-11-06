using System.Reflection;
using OpenMir2.Enums;
using ScriptSystem.Consts;
using ScriptSystem.Processings;
using SystemModule;
using SystemModule.Actors;

namespace GameSrvTest;

public class PoseGenderConditionTests
{
    [Test]
    public void FacingNpc_ReturnsFalseWithoutTryingToCastToPlayer()
    {
        var npc = DispatchProxy.Create<INormNpc, PoseActorProxy>();
        Assert.That(Evaluate(npc, "MAN"), Is.False);
    }

    [Test]
    public void NoFacingActor_ReturnsFalse()
    {
        Assert.That(Evaluate(null, "MAN"), Is.False);
    }

    [TestCase("MAN", PlayerGender.Man, true)]
    [TestCase("MAN", PlayerGender.WoMan, false)]
    [TestCase("WOMAN", PlayerGender.WoMan, true)]
    [TestCase("WOMAN", PlayerGender.Man, false)]
    [TestCase("男", PlayerGender.Man, true)]
    [TestCase("女", PlayerGender.WoMan, true)]
    public void FacingPlayer_OnlyMatchesRequestedGender(string condition, PlayerGender gender, bool expected)
    {
        var target = DispatchProxy.Create<IPlayerActor, PoseActorProxy>();
        ((PoseActorProxy)(object)target).Gender = gender;
        Assert.That(Evaluate(target, condition), Is.EqualTo(expected));
    }

    private static bool Evaluate(IActor? target, string condition)
    {
        var player = DispatchProxy.Create<IPlayerActor, PoseActorProxy>();
        ((PoseActorProxy)(object)player).Target = target;
        var processing = new ConditionProcessingSys();
        processing.Initialize();
        bool success = true; // The condition must clear an earlier successful result too.
        processing.Execute(null!, player,
            new QuestConditionInfo { CmdCode = (int)ConditionCode.CHECKPOSEGENDER, sParam1 = condition }, ref success);
        return success;
    }

    public class PoseActorProxy : DispatchProxy
    {
        public IActor? Target { get; set; }
        public PlayerGender Gender { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IActor.GetPoseCreate) => Target,
            "get_Gender" => Gender,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }
}
