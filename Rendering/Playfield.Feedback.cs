using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // Judgements, hit animations, audio feedback, and timing feedback.

    private JudgementTier RegisterHit(double offsetMs, ChartEvent? eventForScore = null)
    {
        if (_performanceDebugInstrumentFrame)
        {
            _debugLastTimingOffsetMs = offsetMs;
            _debugTimingOffsets[_debugTimingOffsetWriteIndex] = offsetMs;
            _debugTimingOffsetWriteIndex = (_debugTimingOffsetWriteIndex + 1) % _debugTimingOffsets.Length;
            _debugTimingOffsetCount = Math.Min(_debugTimingOffsetCount + 1, _debugTimingOffsets.Length);
        }

        var tier = GetJudgementTier(offsetMs);
        ApplyJudgement(tier, offsetMs, eventForScore);
        RecordHitDelta(offsetMs, tier);
        RecordAccuracy(tier, offsetMs, eventForScore);
        if (tier != JudgementTier.Miss)
        {
            _combo++;
            _maxCombo = Math.Max(_maxCombo, _combo);
            TriggerComboPulse();
            if (eventForScore is not null)
                AwardEventScore(eventForScore, tier);

            if (eventForScore is not null)
            {
                RefreshSkinGameState();
                _skinRuntime.OnNoteHit(BuildSkinNoteView(eventForScore, hit: true, missed: false),
                    SkinJudgementName(tier), offsetMs);
                _skinRuntime.OnComboChanged(_combo);
            }
        }
        if (_superFlickDebugEnabled)
        {
            var currentTick = _tempo is null ? 0 : _tempo.SecondsToTick(_playSeconds);
            SuperFlickDebugLog($"JUDGEMENT HIT tier={tier} offsetMs={offsetMs:0.###} currentTick={currentTick:0.###} gear={SuperFlickDebugGearState()}");
        }
        return tier;
    }

    private JudgementTier GetJudgementTier(double offsetMs)
    {
        var abs = Math.Abs(offsetMs);
        if (abs <= PerfectPlusWindowMs)
            return JudgementTier.PerfectPlus;
        if (abs <= PerfectWindowMs)
            return JudgementTier.Perfect;
        if (abs <= GoodWindowMs)
            return JudgementTier.Good;
        return JudgementTier.Miss;
    }

    private void ShowJudgement(JudgementTier tier, ChartEvent? eventForScore = null)
    {
        ApplyJudgement(tier, null, eventForScore);
        if (tier != JudgementTier.Miss && eventForScore is not null)
            AwardEventScore(eventForScore, tier);
    }

    private void StartHitAnimation(ChartEvent ev)
    {
        if (_tempo is null || _chart is null)
            return;

        var isLinkHead = ev.NoteType == NoteType.LinkHead;
        var isLinkTail = ev.NoteType == NoteType.LinkTail;
        var isHold = ev.IsHoldNote;
        var isTerminalLinkTail = isLinkTail && IsTerminalLinkTail(ev);
        // Link heads own the chain intro at the starting lane. Intermediate tails
        // own only the looping body at their destination lanes. The terminal tail
        // owns the loop plus the normal ending frames.
        var loopOnly = isLinkTail && !isTerminalLinkTail;

        // The editor's 2D view has its own hit-animation settings. Keep the
        // judgement effect aligned with whichever render config drives the lanes.
        var viewRenderConfig = GetActiveViewRenderConfig();
        HitAnimationConfig config;
        string variant;

        if (ev.IsSuperFlick)
        {
            var family = ev.GetSuperFlickFamily(_gear?.AtTick(ev.Tick, includeTickChanges: false));
            config = family == SuperFlickFamily.Inner
                ? viewRenderConfig.HitAnimation.SuperFlick.Inner
                : viewRenderConfig.HitAnimation.SuperFlick.Outer;
            variant = family == SuperFlickFamily.Inner ? "superFlickInner" : "superFlickOuter";
        }
        else if (isLinkHead || isLinkTail || isHold)
        {
            config = viewRenderConfig.HitAnimation.Long;
            variant = "long";
        }
        else
        {
            var counts = SimultaneousCounts(RenderGearState(_tempo.SecondsToTick(_playSeconds)));
            var isMulti = IsSimultaneous(ev, counts);
            if (isMulti)
            {
                config = viewRenderConfig.HitAnimation.Multi;
                variant = "multi";
            }
            else
            {
                config = viewRenderConfig.HitAnimation.Regular;
                variant = "regular";
            }
        }

        if (!config.Enabled)
            return;

        var frameCount = Math.Max(1, config.FrameCount);
        var totalDuration = Math.Max(0.001f, config.TotalDurationSeconds);
        var loopStart = Math.Clamp(config.HoldLoopStartFrame, 1, frameCount);
        var loopEnd = Math.Clamp(config.HoldLoopEndFrame, loopStart, frameCount);

        // Capture the judge position at the instant of the judgement. The
        // animation is deliberately a screen-space sprite by default: camera
        // lane rotation does not rotate it unless RotateWithLane is enabled.
        var state = RenderGearState(_tempo.SecondsToTick(_playSeconds));
        var point = _projection.Project(ev.Track, _playSeconds, _playSeconds, state);
        var trackPosition = GearState.Position(ev.Track);
        var position = point.Position;
        var rotation = 0f;
        if (trackPosition is { } trackInfo)
        {
            // Judge animation remains a 2D billboard, but its anchor is the exact
            // judge point on the same 3D lane surface as the notes.
            var normalizedX = _projection.LaneNormalizedX(ev.Track);
            var judgeBasis = _projection.BuildStandingNoteBasis(
                normalizedX, 0f, trackInfo.Side, trackInfo.Bank);
            position = _projection.ScreenPositionFromCameraSpace(judgeBasis.Center);
        }

        if (config.RotateWithLane)
        {
            rotation = _projection.SpriteRotationRadians(
                trackPosition?.Side ?? (position.X < _projection.JudgeX ? "left" : "right"),
                trackPosition?.Bank,
                point.Depth);
        }

        var holdEndSeconds = 0.0;
        var stopAtHoldEnd = false;
        ChartEventKey? holdTransitionKey = null;
        if (isHold)
        {
            holdEndSeconds = _tempo.TickToSeconds(ev.EndTick);
        }
        else if (isLinkHead)
        {
            // The head plays frames 1..loopStart-1 at the source lane, then loops
            // until the first destination tail. It must disappear at that switch
            // without playing the ending frames; the next LinkTail starts the loop
            // on the new lane.
            if (_nextLinkByHeadKey.TryGetValue(KeyOf(ev), out var nextLink) && nextLink.Tail.Tick >= ev.Tick)
            {
                // In gameplay, keep the source LinkHead loop alive until the
                // destination LinkTail is actually judged. In autoplay there is no
                // gameplay _hitEvents entry for that LinkTail, so use its authored
                // tick instead of creating a transition dependency that can never
                // resolve.
                holdEndSeconds = _tempo.TickToSeconds(nextLink.Tail.Tick);
                stopAtHoldEnd = _gameplayMode;
                holdTransitionKey = _gameplayMode ? KeyOf(nextLink.Tail) : null;
            }
            else if (ev.Duration > 6)
            {
                // A LinkHead without a following tail behaves like a normal long
                // note rather than being left in a permanent loop.
                holdEndSeconds = _tempo.TickToSeconds(ev.EndTick);
            }
            else
            {
                holdEndSeconds = _playSeconds + totalDuration;
            }
        }
        else if (isLinkTail)
        {
            // Each LinkTail owns only its destination segment. Intermediate tails
            // stop exactly when the next tail is judged; the terminal tail continues
            // through the normal long-note ending frames.
            if (_nextLinkByHeadKey.TryGetValue(KeyOf(ev), out var nextLink) && nextLink.Tail.Tick >= ev.Tick)
            {
                holdEndSeconds = _tempo.TickToSeconds(nextLink.Tail.Tick);
                if (_gameplayMode)
                {
                    stopAtHoldEnd = true;
                    holdTransitionKey = KeyOf(nextLink.Tail);
                }
            }
            else if (ev.Duration > 6)
                holdEndSeconds = _tempo.TickToSeconds(ev.EndTick);
            else if (isTerminalLinkTail)
                holdEndSeconds = _tempo.TickToSeconds(ev.Tick);
            else
                holdEndSeconds = _playSeconds + totalDuration;
        }

        var baseWidth = Math.Max(1f, config.Width * point.Scale * point.LaneRotationDepthScale);
        ChartEventKey? animationRootKey = null;
        if (isHold || isLinkHead)
        {
            animationRootKey = KeyOf(ev);
        }
        else if (isLinkTail)
        {
            var root = FindLinkRootHead(ev);
            animationRootKey = root is null ? KeyOf(ev) : KeyOf(root);
        }

        _hitAnimations.Add(new HitAnimationState
        {
            Variant = variant,
            Position = position,
            BaseWidth = baseWidth,
            Rotation = rotation,
            FrameCount = frameCount,
            TotalDurationSeconds = totalDuration,
            IsHold = isHold || isLinkHead || isLinkTail,
            HoldEndSeconds = holdEndSeconds,
            HoldRootKey = animationRootKey,
            HoldLoopStartFrame = loopStart,
            HoldLoopEndFrame = loopEnd,
            HoldUsesLoop = config.HoldUsesLoop,
            LoopOnly = loopOnly,
            IsLinkAnimation = isLinkHead || isLinkTail,
            IsTerminalLinkTail = isTerminalLinkTail,
            StopAtHoldEnd = stopAtHoldEnd,
            HoldTransitionKey = holdTransitionKey,
            ElapsedSeconds = 0
        });
    }

    private int GetHitAnimationFrame(HitAnimationState animation)
    {
        var frameCount = Math.Max(1, animation.FrameCount);
        var totalDuration = Math.Max(0.001f, animation.TotalDurationSeconds);
        var frameDuration = totalDuration / frameCount;
        var elapsedFrames = (int)Math.Floor(animation.ElapsedSeconds / frameDuration);

        if (animation.LoopOnly)
        {
            if (_playSeconds >= animation.HoldEndSeconds)
                return animation.HoldLoopEndFrame;

            var loopStart = Math.Clamp(animation.HoldLoopStartFrame, 1, frameCount);
            var loopEnd = Math.Clamp(animation.HoldLoopEndFrame, loopStart, frameCount);
            var loopBodyLength = loopEnd - loopStart + 1;
            return loopStart + (elapsedFrames % loopBodyLength);
        }

        var introEnd = Math.Max(0, animation.HoldLoopStartFrame - 1);

        if (!animation.IsHold || !animation.HoldUsesLoop || animation.HoldLoopStartFrame >= animation.HoldLoopEndFrame)
            return elapsedFrames + 1;

        if (animation.PostLoopStartElapsedSeconds.HasValue)
        {
            var postElapsedFrames = (int)Math.Floor(
                Math.Max(0, animation.ElapsedSeconds - animation.PostLoopStartElapsedSeconds.Value) / frameDuration);
            return animation.HoldLoopEndFrame + 1 + postElapsedFrames;
        }

        var reachedHoldEnd = animation.HoldTransitionKey.HasValue
            ? _hitEvents.Contains(animation.HoldTransitionKey.Value) ||
              _playSeconds >= animation.HoldEndSeconds + GameplayHitWindowSeconds
            : _playSeconds >= animation.HoldEndSeconds;

        if (reachedHoldEnd)
        {
            // A non-terminal LinkHead ends when the connected LinkTail is actually
            // judged. This keeps the loop alive through a late-but-good transition
            // instead of making the animation disappear at the authored tick.
            if (animation.StopAtHoldEnd)
            {
                if (animation.HoldTransitionKey.HasValue &&
                    !_hitEvents.Contains(animation.HoldTransitionKey.Value))
                    return animation.HoldLoopEndFrame;

                // Once the connected LinkTail is judged, stop the source animation
                // without playing the terminal ending frames. The destination tail
                // owns the next animation.
                return animation.HoldLoopEndFrame;
            }

            // The hold has just ended. Start the non-looping tail of the animation
            // at the frame after the loop, regardless of how much wall-clock time
            // was spent looping on the hold body.
            animation.PostLoopStartElapsedSeconds = animation.ElapsedSeconds;
            return Math.Min(frameCount, animation.HoldLoopEndFrame + 1);
        }

        if (elapsedFrames < introEnd)
            return Math.Min(animation.HoldLoopStartFrame - 1, elapsedFrames + 1);

        var loopLength = animation.HoldLoopEndFrame - animation.HoldLoopStartFrame + 1;
        var loopFrame = (elapsedFrames - introEnd) % loopLength;
        return animation.HoldLoopStartFrame + loopFrame;
    }

    private void UpdateHitAnimations(double dt)
    {
        if (_hitAnimations.Count == 0)
            return;

        for (var i = _hitAnimations.Count - 1; i >= 0; i--)
        {
            var animation = _hitAnimations[i];
            if (animation.Cancelled)
            {
                _hitAnimations.RemoveAt(i);
                continue;
            }

            animation.ElapsedSeconds += Math.Max(0, dt);
            var frame = GetHitAnimationFrame(animation);
            var reachedHoldEnd = animation.HoldTransitionKey.HasValue
                ? _hitEvents.Contains(animation.HoldTransitionKey.Value) ||
                  _playSeconds >= animation.HoldEndSeconds + GameplayHitWindowSeconds
                : _playSeconds >= animation.HoldEndSeconds;
            if ((animation.StopAtHoldEnd && reachedHoldEnd) ||
                (animation.LoopOnly && _playSeconds >= animation.HoldEndSeconds) ||
                (!animation.LoopOnly && frame >= animation.FrameCount))
                _hitAnimations.RemoveAt(i);
        }
    }

    private void DrawHitAnimations()
    {
        if (_hitAnimations.Count == 0)
            return;

        _spriteBatch.Begin(
            samplerState: SamplerState.LinearClamp,
            blendState: BlendState.NonPremultiplied);

        foreach (var animation in _hitAnimations)
        {
            var frame = GetHitAnimationFrame(animation);
            var texture = _textures.GetHitFrame(animation.Variant, frame);
            if (texture is null)
                continue;

            var scale = animation.BaseWidth / Math.Max(1, texture.Width);
            var origin = new Vector2(texture.Width * 0.5f, texture.Height * 0.5f);
            _spriteBatch.Draw(
                texture,
                animation.Position,
                null,
                Color.White,
                animation.Rotation,
                origin,
                scale,
                SpriteEffects.None,
                0f);
        }

        _spriteBatch.End();
    }

    private void ApplyJudgement(JudgementTier tier, double? offsetMs = null, ChartEvent? eventForAnimation = null)
    {
        if (tier != JudgementTier.PerfectPlus)

        switch (tier)
        {
            case JudgementTier.PerfectPlus:
                _lastJudgement = "PERFECT+";
                _lastJudgementColor = Color.White;
                break;
            case JudgementTier.Perfect:
                _lastJudgement = "PERFECT";
                _lastJudgementColor = Color.Yellow;
                break;
            case JudgementTier.Good:
                _lastJudgement = "GOOD";
                _lastJudgementColor = new Color(128, 224, 255);
                break;
            default:
                _lastJudgement = "MISS";
                _lastJudgementColor = Color.Red;
                break;
        }
        StartJudgementUiLifetime();
        if (tier != JudgementTier.Miss && eventForAnimation is not null)
            StartHitAnimation(eventForAnimation);
        if (offsetMs.HasValue)
            StartTimingUi(offsetMs.Value, tier);
    }

    private void RegisterMiss(string source = "unknown", ChartEvent? ev = null, bool recordAccuracy = true)
    {
        _lastJudgement = "MISS";
        _lastJudgementColor = Color.Red;
        _combo = 0;
        var offsetMs = GetCurrentOffsetMs(ev);
        StartJudgementUiLifetime();
        if (offsetMs.HasValue)
            StartTimingUi(offsetMs.Value, JudgementTier.Miss);
        RecordHitDelta(offsetMs ?? 0.0, JudgementTier.Miss);
        if (recordAccuracy)
            RecordAccuracy(JudgementTier.Miss, offsetMs ?? 0.0, ev);
        ApplyGaugeMiss(ev);
        if (_hardGauge <= 0.0)
            BeginHardGaugeGameOver();
        if (ev is not null)
        {
            RefreshSkinGameState();
            _skinRuntime.OnNoteMiss(BuildSkinNoteView(ev, hit: false, missed: true), offsetMs ?? 0.0);
            _skinRuntime.OnComboChanged(_combo);
        }
        if (_superFlickDebugEnabled)
        {
            var tick = ev?.Tick.ToString(CultureInfo.InvariantCulture) ?? "-";
            var track = ev?.Track.ToString(CultureInfo.InvariantCulture) ?? "-";
            var type = ev?.NoteType.ToString() ?? "-";
            var currentTick = _tempo is null ? 0 : _tempo.SecondsToTick(_playSeconds);
            SuperFlickDebugLog($"JUDGEMENT MISS source={source} eventTick={tick} track={track} type={type} currentTick={currentTick:0.###} gear={SuperFlickDebugGearState()}");
        }
    }

    private double? GetCurrentOffsetMs(ChartEvent? ev)
    {
        if (ev is null || _tempo is null)
            return null;
        return (_playSeconds - _tempo.TickToSeconds(ev.Tick)) * 1000.0;
    }

    private static string SkinJudgementName(JudgementTier tier) => tier switch
    {
        JudgementTier.PerfectPlus => "perfect_plus",
        JudgementTier.Perfect => "perfect",
        JudgementTier.Good => "good",
        _ => "miss"
    };

    private SkinNoteView BuildSkinNoteView(ChartEvent ev, bool hit, bool missed)
    {
        var position = GearState.Position(ev.Track);
        var side = position?.Side ?? "";
        var bank = position?.Bank ?? "";
        var lane = position.HasValue ? position.Value.LocalPos + 1 : 0;

        var currentTick = _tempo?.SecondsToTick(_playSeconds) ?? ev.Tick;
        var renderState = RenderGearState(currentTick);
        var point = _projection.Project(ev.Track, _playSeconds, _playSeconds, renderState);
        var rotation = position is null
            ? 0f
            : _projection.SpriteRotationRadians(side, bank, point.Depth);

        var noteSeconds = _tempo?.TickToSeconds(ev.Tick) ?? _playSeconds;
        var endSeconds = _tempo?.TickToSeconds(ev.EndTick) ?? noteSeconds;
        var duration = Math.Max(0.0, endSeconds - noteSeconds);
        var progress = duration > 0.000001
            ? Math.Clamp((_playSeconds - noteSeconds) / duration, 0.0, 1.0)
            : hit || missed ? 1.0 : 0.0;

        var type = ev.NoteType switch
        {
            NoteType.SingleLong => "indicator-long",
            NoteType.Vibrato => "indicator-vibrato",
            NoteType.LinkHead => "link_head",
            NoteType.LinkTail => "link_tail",
            _ when ev.IsSuperFlick => "superFlick",
            _ => "tap"
        };

        var active = IsActiveEvent(ev);
        return new SkinNoteView(
            type,
            noteSeconds,
            duration,
            lane,
            side,
            bank,
            new SkinVector2View(point.Position.X, point.Position.Y),
            rotation,
            point.Scale,
            progress,
            hit,
            missed,
            active,
            !OutsideNoteView(point, point),
            ev.IsHoldNote,
            ev.NoteType is NoteType.LinkHead or NoteType.LinkTail,
            ev.IsSuperFlick);
    }

    private void StartTimingUi(double offsetMs, JudgementTier tier)
    {
        _lastTimingOffsetMs = offsetMs;
        _hasTimingOffset = true;

        var earlyLateConfig = _skin.UIConfig.EarlyLate;
        _earlyLatePulseTimer = SkinUIExpression.ResolveSeconds(earlyLateConfig.PulseFadeOutDuration, null, 0f);
        _earlyLateUiTimer = GetFeedbackUiLifetime(earlyLateConfig);

        var timingConfig = _skin.UIConfig.TimingOffset;
        _timingOffsetPulseTimer = SkinUIExpression.ResolveSeconds(timingConfig.PulseFadeOutDuration, null, 0f);
        _timingOffsetUiTimer = GetFeedbackUiLifetime(timingConfig);
    }

    private void RecordHitDelta(double offsetMs, JudgementTier tier)
    {
        var config = _skin.UIConfig.HitDeltaBar;
        _hitDeltaSamples.Add(new HitDeltaSample(offsetMs, tier, GetHitDeltaLifetime(config)));
        var maxEntries = Math.Max(1, config.MaxEntries);
        if (_hitDeltaSamples.Count > maxEntries)
            _hitDeltaSamples.RemoveRange(0, _hitDeltaSamples.Count - maxEntries);
    }

    private static Color TimingDirectionColor(double offsetMs)
    {
        if (offsetMs < -0.01)
            return new Color(105, 185, 255);
        if (offsetMs > 0.01)
            return new Color(255, 105, 105);
        return Color.White;
    }
}
