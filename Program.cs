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

public class Player
{
    public string ConnectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Card> Hand { get; set; } = new();
    public bool HasActed { get; set; } = false;
    public int TotalScore { get; set; } = 0; // 累計スコア
    public int LastRoundScore { get; set; } = 0; // 最新ラウンドのスコア
}

public class GameSession
{
    public string RoomId { get; set; } = "";
    public List<Card> Deck = new();
    public List<Card> Field = new();
    public List<Player> Players = new();
    public int CurrentTurnIndex = 0;
    public int? DabutoPlayerIndex = null;
    public int? RoundStarterIndex = null; // 次の回の親
    public int ConsecutivePasses = 0;
    public bool IsStarted = false;

    public void StartGame(bool isFirstRound)
    {
        var suits = new[] { "♠", "♥", "♦", "♣" };
        Deck = suits.SelectMany(s => Enumerable.Range(1, 13).Select(r => new Card(s, r))).OrderBy(_ => Guid.NewGuid()).ToList();
        Field = Deck.Take(5).ToList(); Deck.RemoveRange(0, 5);

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

    // サーバー側でのスコア計算
    public int CalculateScore(List<Card> hand)
    {
        var ranks = hand.Select(c => c.Rank).Distinct().OrderBy(r => r).ToList();
        if (ranks.Count < 5) return 0;
        var extended = ranks.Concat(ranks.Select(r => r + 13)).OrderBy(r => r).ToList();
        bool straight = false;
        for (int i = 0; i <= extended.Count - 5; i++)
        {
            if (extended[i + 4] == extended[i] + 4) { straight = true; break; }
        }
        if (!straight) return 0;
        return hand.GroupBy(c => c.Suit).Max(g => g.Count()) * 20;
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

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveRoom();
        await base.OnDisconnectedAsync(exception);
    }

    public Task<bool> CheckRoom(string roomId) => Task.FromResult(_gs.Rooms.ContainsKey(roomId));

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
                        _gs.Rooms.TryRemove(roomId, out _);
                    }
                    else if (room.IsStarted)
                    {
                        await Clients.Group(roomId).SendAsync("RoomAborted", $"{player.Name} が退出したため、ゲームを終了しました。");
                        _gs.Rooms.TryRemove(roomId, out _);
                    }
                    else
                    {
                        await Clients.Group(roomId).SendAsync("UpdatePlayers", room.Players.Select(p => p.Name));
                    }
                }
            }
            _gs.ConnectionToRoom.TryRemove(Context.ConnectionId, out _);
        }
    }

    public async Task JoinRoom(string name, string roomId, bool create)
    {
        await LeaveRoom();
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
        if (room.Players.Count < 1) return;

        room.StartGame(room.RoundStarterIndex == null); // 初回かどうか判定
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

        // 100点（強制ストップ）のチェック
        int score = room.CalculateScore(player.Hand);
        if (score == 100)
        {
            room.RoundStarterIndex = room.CurrentTurnIndex; // 100点上がりの人が次の親
            await FinishRound(room);
            return;
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
        if (player.ConnectionId != Context.ConnectionId || !player.HasActed) return;

        player.HasActed = false;
        room.CurrentTurnIndex = (room.CurrentTurnIndex + 1) % room.Players.Count;

        if (room.CurrentTurnIndex == room.DabutoPlayerIndex)
        {
            room.RoundStarterIndex = room.DabutoPlayerIndex; // ダブトした人が次の親
            await FinishRound(room);
        }
        else
        {
            await BroadcastState(roomId);
        }
    }

    private async Task FinishRound(GameSession room)
    {
        // スコアの集計とペナルティ判定
        int maxScore = room.Players.Max(p => room.CalculateScore(p.Hand));
        foreach (var p in room.Players)
        {
            int score = room.CalculateScore(p.Hand);
            // ダブト失敗ペナルティ
            if (room.DabutoPlayerIndex.HasValue && room.Players[room.DabutoPlayerIndex.Value] == p && score < maxScore)
            {
                score = -score;
            }
            p.LastRoundScore = score;
            p.TotalScore += score;
        }

        room.IsStarted = false; // 次のラウンド待機へ
        await Clients.Group(room.RoomId).SendAsync("GameEnded", room.Players.Select(p => new {
            name = p.Name,
            lastScore = p.LastRoundScore,
            totalScore = p.TotalScore
        }));
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