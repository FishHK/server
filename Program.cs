using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddCors();
builder.Services.AddSingleton<GameService>();

builder.Services.AddResponseCompression();

var app = builder.Build();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors(policy => policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials());
app.MapHub<GameHub>("/gamehub");
app.Run();

public record Card(string Suit, int Rank);
// --- Playerクラスに累計スコアを追加 ---
public class Player
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Card> Hand { get; set; } = new();
    public bool HasActed { get; set; } = false;
    public int TotalScore { get; set; } = 0; // 累計スコア用
}

public class GameSession
{
    public string RoomId { get; set; } = "";
    public List<Card> Deck = new();
    public List<Card> Field = new();
    public List<Player> Players = new();
    public int CurrentTurnIndex = 0;
    public int? DabutoPlayerIndex = null;
    public int? RoundStarterIndex = null; // 次の回の開始プレイヤー
    public int ConsecutivePasses = 0;
    public bool IsStarted = false;

    public void StartGame(bool isFirstRound)
    {
        var suits = new[] { "♠", "♥", "♦", "♣" };
        Deck = suits.SelectMany(s => Enumerable.Range(1, 13).Select(r => new Card(s, r))).OrderBy(_ => Guid.NewGuid()).ToList();
        Field = Deck.Take(5).ToList(); Deck.RemoveRange(0, 5);

        // 初回はランダム、2回目以降は前回のアタッカーが親
        if (isFirstRound || RoundStarterIndex == null)
        {
            CurrentTurnIndex = Random.Shared.Next(Players.Count);
        }
        else
        {
            CurrentTurnIndex = RoundStarterIndex.Value;
        }

        foreach (var p in Players)
        {
            p.Hand = Deck.Take(5).OrderBy(c => c.Suit).ThenBy(c => c.Rank).ToList();
            Deck.RemoveRange(0, 5);
            p.HasActed = false;
        }
        IsStarted = true;
        DabutoPlayerIndex = null;
        ConsecutivePasses = 0;
    }
}

public class GameService
{
    public ConcurrentDictionary<string, GameSession> Rooms = new();
    public ConcurrentDictionary<string, string> ConnectionToRoom = new();
}

public class GameHub : Hub
{
    private readonly GameService _gs;
    public GameHub(GameService gs) => _gs = gs;

    // 通信切断時の自動退出処理
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveRoom();
        await base.OnDisconnectedAsync(exception);
    }

    public Task<bool> CheckRoom(string roomId) => Task.FromResult(_gs.Rooms.ContainsKey(roomId));

    // ルームから退出する処理
    public async Task LeaveRoom()
    {
        if (_gs.ConnectionToRoom.TryGetValue(Context.ConnectionId, out var roomId))
        {
            if (_gs.Rooms.TryGetValue(roomId, out var room))
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
                if (player != null)
                {
                    room.Players.Remove(player);
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);

                    if (room.Players.Count == 0)
                    {
                        _gs.Rooms.TryRemove(roomId, out _); // 誰もいなくなったら部屋を消滅
                    }
                    else
                    {
                        if (room.IsStarted)
                        {
                            // プレイ中に誰かが抜けたら、進行不能になるため強制終了
                            await Clients.Group(roomId).SendAsync("RoomAborted", $"{player.Name} が退出したため、ゲームを終了しました。");
                            _gs.Rooms.TryRemove(roomId, out _);
                        }
                        else
                        {
                            // ロビー待機中なら残りのメンバーリストを更新
                            await Clients.Group(roomId).SendAsync("UpdatePlayers", room.Players.Select(p => p.Name));
                        }
                    }
                }
            }
            _gs.ConnectionToRoom.TryRemove(Context.ConnectionId, out _);
        }
    }

    public async Task JoinRoom(string name, string roomId, bool create)
    {
        await LeaveRoom(); // もし既に別の部屋にいたら、まず抜ける（増殖バグ対策）

        if (!_gs.Rooms.ContainsKey(roomId))
        {
            if (create) _gs.Rooms[roomId] = new GameSession { RoomId = roomId };
            else return;
        }
        var room = _gs.Rooms[roomId];
        if (room.IsStarted) return;

        room.Players.Add(new Player { ConnectionId = Context.ConnectionId, Name = name });
        _gs.ConnectionToRoom[Context.ConnectionId] = roomId;

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UpdatePlayers", room.Players.Select(p => p.Name));
    }

    public async Task StartGame()
    {
        if (!_gs.ConnectionToRoom.TryGetValue(Context.ConnectionId, out var roomId)) return;
        var room = _gs.Rooms[roomId];
        // 初回かどうかを判定（RoundStarterIndexが未設定なら初回）
        room.StartGame(room.RoundStarterIndex == null);
        await BroadcastState(roomId);
    }

    public async Task PerformAction(string type, int? handIdx, int? fieldIdx)
    {
        if (!_gs.ConnectionToRoom.TryGetValue(Context.ConnectionId, out var roomId)) return;
        var room = _gs.Rooms[roomId];
        var player = room.Players[room.CurrentTurnIndex];

        if (player.ConnectionId != Context.ConnectionId || player.HasActed) return;

        if (type == "ExchangeOne" && handIdx.HasValue && fieldIdx.HasValue)
        {
            var temp = room.Field[fieldIdx.Value];
            room.Field[fieldIdx.Value] = player.Hand[handIdx.Value];
            player.Hand[handIdx.Value] = temp;
            room.ConsecutivePasses = 0; player.HasActed = true;
        }
        else if (type == "ExchangeAll")
        {
            var temp = room.Field.ToList();
            room.Field = player.Hand.ToList();
            player.Hand = temp;
            room.ConsecutivePasses = 0; player.HasActed = true;
        }
        else if (type == "Pass")
        {
            room.ConsecutivePasses++;
            if (room.ConsecutivePasses >= room.Players.Count)
            {
                room.Deck.AddRange(room.Field);
                room.Field = room.Deck.Take(5).ToList();
                room.Deck.RemoveRange(0, 5);
                room.ConsecutivePasses = 0;
            }
            player.HasActed = true;
        }
        await BroadcastState(roomId);
    }

    public async Task DeclareDabuto()
    {
        if (!_gs.ConnectionToRoom.TryGetValue(Context.ConnectionId, out var roomId)) return;
        var room = _gs.Rooms[roomId];
        if (room.Players[room.CurrentTurnIndex].ConnectionId != Context.ConnectionId) return;

        if (room.DabutoPlayerIndex == null) room.DabutoPlayerIndex = room.CurrentTurnIndex;
        await BroadcastState(roomId);
    }

    public async Task EndTurn()
    {
        if (!_gs.ConnectionToRoom.TryGetValue(Context.ConnectionId, out var roomId)) return;
        var room = _gs.Rooms[roomId];
        var player = room.Players[room.CurrentTurnIndex];

        // 100点ストップのチェックをここで厳密に行う
        // (PerformAction内で100点になったら即座にここへ誘導するロジックも可)

        player.HasActed = false;
        room.CurrentTurnIndex = (room.CurrentTurnIndex + 1) % room.Players.Count;

        if (room.CurrentTurnIndex == room.DabutoPlayerIndex)
        {
            await FinishRound(room);
        }
        else
        {
            await BroadcastState(roomId);
        }
    }

    private async Task FinishRound(GameSession room)
    {
        // ダブト宣言者またはストップ者を次回の親に設定
        // (ストップ実装時はそのプレイヤーのIndexを入れる)
        room.RoundStarterIndex = room.DabutoPlayerIndex ?? room.CurrentTurnIndex;

        // スコア加算ロジック
        // ※前回のスコア計算ロジックをサーバー側に集約して呼び出す
        await Clients.Group(room.RoomId).SendAsync("GameEnded", room.Players);
        room.IsStarted = false; // 次のラウンド開始待ち状態へ
    }

    private async Task BroadcastState(string roomId)
    {
        if (!_gs.Rooms.TryGetValue(roomId, out var room)) return;
        foreach (var p in room.Players)
        {
            await Clients.Client(p.ConnectionId).SendAsync("ReceiveState", new
            {
                Field = room.Field,
                Hand = p.Hand,
                CurrentPlayerName = room.Players[room.CurrentTurnIndex].Name,
                IsYourTurn = (p.ConnectionId == room.Players[room.CurrentTurnIndex].ConnectionId),
                HasActed = p.HasActed,
                DabutoName = room.DabutoPlayerIndex.HasValue ? room.Players[room.DabutoPlayerIndex.Value].Name : null,
                DeckCount = room.Deck.Count
            });
        }
    }
}