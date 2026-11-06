using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using GameSrv.Services;
using GameSrv.Word.Threads;
using M2Server;
using OpenMir2;
using OpenMir2.Data;
using OpenMir2.Packets.ServerPackets;
using Serilog;
using SystemModule.Data;
using SystemModule.SubSystem;

namespace GameSrvTest;

[NonParallelizable]
public class PlayerSaveQueueTests
{
    private IFrontEngine? _previousFrontEngine;
    private object? _previousDbLock;
    private ILogger? _previousLogger;
    private FrontEngine _front = null!;
    private ConcurrentQueue<int> _saveQueries = null!;
    private ConcurrentDictionary<int, ServerRequestData> _responses = null!;

    [SetUp]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _previousLogger = LogService.Logger;
        LogService.Logger = new LoggerConfiguration().CreateLogger();
        _previousFrontEngine = M2Share.FrontEngine;
        _previousDbLock = M2Share.UserDBCriticalSection;
        M2Share.FrontEngine = _front = new FrontEngine();
        M2Share.UserDBCriticalSection = new object();
        _saveQueries = GetStatic<ConcurrentQueue<int>>("SaveProcessList");
        _responses = GetStatic<ConcurrentDictionary<int, ServerRequestData>>("QueryMap");
        _saveQueries.Clear();
        _responses.Clear();
    }

    [TearDown]
    public void Cleanup()
    {
        _saveQueries.Clear();
        _responses.Clear();
        (LogService.Logger as IDisposable)?.Dispose();
        LogService.Logger = _previousLogger!;
        M2Share.FrontEngine = _previousFrontEngine!;
        M2Share.UserDBCriticalSection = _previousDbLock!;
    }

    [Test]
    public void SuccessfulAck_OnlyReleasesMatchingSaveAndAllowsCharacterToLoadAgain()
    {
        AddPendingSave(11, "已下线角色");
        AddPendingSave(12, "仍在保存角色");
        Respond(11, Messages.DBR_SAVEHUMANRCD, 1);

        PlayerDataService.ProcessSaveQueue();

        Assert.That(_front.InSaveRcdList("已下线角色"), Is.False);
        Assert.That(_front.InSaveRcdList("仍在保存角色"), Is.True);
        Assert.That(_front.SaveListCount(), Is.EqualTo(1));
    }

    [TestCase(Messages.DBR_SAVEHUMANRCD, 0)]
    [TestCase(Messages.DBR_LOADHUMANRCD, 1)]
    public void FailedOrUnrelatedAck_KeepsUnsavedCharacterBlocked(int ident, int result)
    {
        AddPendingSave(21, "不能丢弃的保存");
        Respond(21, ident, result);

        PlayerDataService.ProcessSaveQueue();

        Assert.That(_front.InSaveRcdList("不能丢弃的保存"), Is.True);
        Assert.That(_front.SaveListCount(), Is.EqualTo(1));
    }

    [Test]
    public void OutOfOrderAck_DoesNotClearAnotherOutstandingSave()
    {
        AddPendingSave(31, "先提交");
        AddPendingSave(32, "后提交");
        Respond(32, Messages.DBR_SAVEHUMANRCD, 1);
        PlayerDataService.ProcessSaveQueue();
        Assert.That(_front.SaveListCount(), Is.EqualTo(2));

        Respond(31, Messages.DBR_SAVEHUMANRCD, 1);
        PlayerDataService.ProcessSaveQueue();
        Assert.That(_front.InSaveRcdList("先提交"), Is.False);
        Assert.That(_front.InSaveRcdList("后提交"), Is.True);

        PlayerDataService.ProcessSaveQueue();
        Assert.That(_front.IsIdle(), Is.True);
    }

    [Test]
    public void LoadWhileSaveIsPending_RequeuesInsteadOfClosingConnection()
    {
        AddPendingSave(41, "正在重入");
        var load = new LoadDBInfo { ChrName = "正在重入", SessionID = 3 };
        _front.m_LoadRcdTempList.Add(load);
        using var processor = new CharacterDataProcessor();

        // NetChannel is deliberately not configured: entering the old CloseUser branch would fail.
        typeof(CharacterDataProcessor).GetMethod("ProcessReadStorage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(processor, null);

        Assert.That(_front.m_LoadRcdTempList, Is.Empty);
        Assert.That(_front.m_LoadRcdList, Has.Count.EqualTo(1));
        Assert.That(_front.m_LoadRcdList[0], Is.EqualTo(load));
        Assert.That(_front.InSaveRcdList(load.ChrName), Is.True);
    }

    private void AddPendingSave(int queryId, string character)
    {
        _front.AddToSaveRcdList(new SavePlayerRcd { QueryId = queryId, ChrName = character, IsSaveing = true });
        _saveQueries.Enqueue(queryId);
    }

    private static void Respond(int queryId, int ident, int result) => PlayerDataService.Enqueue(queryId,
        new ServerRequestData
        {
            QueryId = queryId,
            Message = EDCode.EncodeBuffer(SerializerUtil.Serialize(new ServerRequestMessage(ident, result, 0, 0, 0))),
            Packet = Array.Empty<byte>(),
            Sign = Array.Empty<byte>()
        });

    private static T GetStatic<T>(string name) =>
        (T)typeof(PlayerDataService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
