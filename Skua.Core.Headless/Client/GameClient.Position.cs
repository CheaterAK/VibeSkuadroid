using Skua.Core.Headless.Maps;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Client;

// Where the avatar stands in its cell, and walking, as the decompiled client does it (Game4000r9.swf):
//   * World.updateUserDisplay places the avatar entering a cell at its leaf's tx/ty when set, else at the pad it came
//     in by (map[strPad].x/.y, plus or minus 5 px at random), else at the stage centre (480, 275). World.moveToCell
//     clears the leaf's tx/ty (unless the pad is "none").
//   * AvatarMC.walkTo moves the clip in a straight line taking round(1000 * distance / (speed * 22)) ms.
//   * World.moveRequest -> pushMove sets the leaf's tx/ty/sp and sends %xt%zm%mv%room%tx%ty%WALKSPEED% (pushMove sends
//     World.speed, which is WALKSPEED, not the clip's speed), at most once per 500 ms (mvTimer, a one-shot Timer(500):
//     a request while it runs replaces the pending one, which goes out when it fires); nothing is sent while bitWalk
//     is off (between moveToCell and the cell being shown, here while the map loads).
// The server keeps positions only to show other players: attacks (gar) and pickups carry no position and the game
// server checks the cell, not the distance (Seraph's live-tested headless client fights and picks up without ever
// walking; World.approachTarget's walk into range is client-side). So positions here follow the client for scripts
// that read or wait on them (Skua's Player.WalkTo waits for pMC.x/y to reach the target), and walks send what the client
// sends; approachTarget still does not walk.
public sealed partial class GameClient
{
    /// <summary>The stage centre the game uses when a cell has no pad of that name (960 x 550 stage).</summary>
    public const int StageCenterX = 960 / 2;

    /// <inheritdoc cref="StageCenterX"/>
    public const int StageCenterY = 550 / 2;

    /// <summary>World.mvTimer: one walk request per this interval.</summary>
    public static readonly TimeSpan MoveRequestInterval = TimeSpan.FromMilliseconds(500);

    private readonly Random _placementRandom = new();
    private (long Generation, string Cell, string Pad)? _placedFor;
    private bool _placedOnPad;
    private bool _walkedSincePlaced;
    private double _fromX = StageCenterX;
    private double _fromY = StageCenterY;
    private double _toX = StageCenterX;
    private double _toY = StageCenterY;
    private long _walkStart;
    private long _walkMs;
    private ITimer? _moveTimer;
    private (int X, int Y, int Speed)? _pendingMove;

    /// <summary>
    /// The avatar's clip position (pMC.x / pMC.y): placed when it enters a cell (pad position from the map file when
    /// known, else the stage centre), moving in a straight line while it walks.
    /// </summary>
    public (int X, int Y) Position => CurrentPosition();

    /// <summary>True while the avatar is walking (AvatarMC.walkOn).</summary>
    public bool IsWalking
    {
        get
        {
            CurrentPosition();
            lock (_gate)
                return _walkMs > 0 && Now - _walkStart < _walkMs;
        }
    }

    /// <summary>
    /// skua.swf Player.walkTo: the clip walks to (<paramref name="x"/>, <paramref name="y"/>) at
    /// <paramref name="speed"/> (8 means World.WALKSPEED) and World.moveRequest tells the server.
    /// </summary>
    public void WalkTo(int x, int y, int speed)
    {
        (int cx, int cy) = CurrentPosition();
        lock (_gate)
        {
            if (speed == 8)
                speed = _walkSpeed;
            double distance = Math.Sqrt(((double)x - cx) * ((double)x - cx) + ((double)y - cy) * ((double)y - cy));
            _fromX = cx;
            _fromY = cy;
            _toX = x;
            _toY = y;
            _walkStart = Now;
            _walkMs = (long)Math.Round(1000 * (distance / (Math.Max(1, speed) * 22.0)));
            _walkedSincePlaced = true;
        }
        MoveRequest(x, y, speed);
    }

    /// <summary>
    /// World.moveRequest: pushMove now when the move timer is idle (and start it), else remember this move as the one
    /// to push when the timer fires. The clip itself does not move (that is <see cref="WalkTo"/>).
    /// </summary>
    public void MoveRequest(int x, int y, int speed)
    {
        bool now;
        lock (_gate)
        {
            if (_disposed)
                return;
            now = _moveTimer is null;
            if (now)
            {
                _moveTimer = _time.CreateTimer(_ => OnMoveTimer(), null, MoveRequestInterval, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _pendingMove = (x, y, speed);
            }
        }
        if (now)
            PushMove(x, y, speed);
    }

    private void OnMoveTimer()
    {
        (int X, int Y, int Speed)? pending;
        lock (_gate)
        {
            _moveTimer?.Dispose();
            _moveTimer = null;
            pending = _pendingMove;
            _pendingMove = null;
        }
        if (pending is { } p)
            PushMove(p.X, p.Y, p.Speed);
    }

    // World.pushMove: uoTreeLeafSet(me, {tx, ty, sp}) and, with bitWalk, the mv packet at World.speed (WALKSPEED).
    private void PushMove(int x, int y, int speed)
    {
        World.Write((w, me) =>
        {
            if (me is not null && ((JsonObject)w["uoTree"]!)[me] is JsonObject leaf)
            {
                leaf["tx"] = x;
                leaf["ty"] = y;
                leaf["sp"] = speed;
            }
        }, "pushMove", touchesItems: false);
        int walkSpeed;
        bool bitWalk;
        lock (_gate)
        {
            walkSpeed = _walkSpeed;
            bitWalk = !_mapLoading;
        }
        if (bitWalk)
            SendXt("mv", null, x, y, walkSpeed);
    }

    private (int X, int Y) CurrentPosition()
    {
        Skua.Core.Headless.World.AreaInfo area = World.GetArea();
        (int tx, int ty) = World.Read((w, me) => me is not null && ((JsonObject)w["uoTree"]!)[me] is JsonObject leaf
            ? (JsonUtil.AsInt(leaf["tx"]), JsonUtil.AsInt(leaf["ty"]))
            : (0, 0));
        lock (_gate)
        {
            (long, string, string) key = (_mapGeneration, area.Cell, area.Pad);
            PadPosition? pad = _cells is not null && _cells.PadPositions.TryGetValue(area.Cell, out IReadOnlyDictionary<string, PadPosition>? onCell)
                && onCell.TryGetValue(area.Pad, out PadPosition p) ? p : null;
            if (_placedFor != key || (!_placedOnPad && !_walkedSincePlaced && pad is not null))
            {
                // World.updateUserDisplay.
                int x, y;
                if (tx != 0 || ty != 0)
                {
                    (x, y) = (tx, ty);
                }
                else if (pad is PadPosition at)
                {
                    x = at.X + _placementRandom.Next(10) - 5;
                    y = at.Y + _placementRandom.Next(10) - 5;
                }
                else
                {
                    (x, y) = (StageCenterX, StageCenterY);
                }
                _placedFor = key;
                _placedOnPad = pad is not null || tx != 0 || ty != 0;
                _walkedSincePlaced = false;
                _fromX = _toX = x;
                _fromY = _toY = y;
                _walkMs = 0;
            }
            if (_walkMs <= 0)
                return ((int)_toX, (int)_toY);
            double t = (Now - _walkStart) / (double)_walkMs;
            if (t >= 1)
                return ((int)_toX, (int)_toY);
            return ((int)Math.Floor(_fromX + (_toX - _fromX) * t), (int)Math.Floor(_fromY + (_toY - _fromY) * t));
        }
    }

    // Under _gate (Reset).
    private void ResetPositionCore()
    {
        _moveTimer?.Dispose();
        _moveTimer = null;
        _pendingMove = null;
        _placedFor = null;
        _placedOnPad = false;
        _walkedSincePlaced = false;
        _fromX = _toX = StageCenterX;
        _fromY = _toY = StageCenterY;
        _walkMs = 0;
    }
}
