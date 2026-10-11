using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunAnimationTransitionChecks(Dictionary<string,bool> checks)
    {
        using var sprite = new SpriteView(); sprite.LoadSkin(_settings.SkinDirectory);
        if (!sprite.HasSmoothTransitions || sprite.WalkingTiming is not { } timing) return;
        var viewport = sprite.ViewportSizeAt(240);
        sprite.Width = sprite.Height = viewport;
        sprite.Measure(new Size(viewport,viewport)); sprite.Arrange(new Rect(0,0,viewport,viewport));
        bool Near(BitmapSource a, BitmapSource b)
        {
            var x=BitmapPixels(a); var y=BitmapPixels(b); long difference=0;
            for (var i=0;i<x.Length;i++) difference+=Math.Abs(x[i]-y[i]);
            return difference/(double)x.Length < .3;
        }
        foreach (var direction in new[]{1,-1})
        {
            sprite.SetBehaviorState(PetState.Idle,null,direction); sprite.ResetAnimation();
            sprite.Advance((sprite.FrameCount(PetState.Idle)-1)/24d);
            var idleLast=RenderSprite(sprite);
            var playback=new WalkingPlayback(timing,4); playback.Begin(3,3);
            sprite.SetBehaviorState(PetState.Walking,playback,direction);
            checks[$"transition_start_frozen_idle_{direction}"]=sprite.TransitionActive&&Near(idleLast,RenderSprite(sprite));
            sprite.Advance(.1);
            var middle=RenderSprite(sprite);
            using (sprite.HoldPlacementPresentation())
            {
                sprite.Advance(.025);
                checks[$"transition_placement_hold_keeps_snapshot_{direction}"]=Near(middle,RenderSprite(sprite));
            }
            var resumed=RenderSprite(sprite);
            checks[$"transition_placement_release_keeps_blend_writable_{direction}"]=sprite.TransitionActive
                &&!BitmapPixels(middle).AsSpan().SequenceEqual(BitmapPixels(resumed));
            middle=resumed;
            checks[$"transition_start_alpha_and_hit_{direction}"]=RenderHasMargin(middle)&&AlphaMatchesRender(sprite,middle);
            SaveBitmap(middle,Path.Combine(_store.DirectoryPath,$"transition-idle-to-walk-{direction}.png"));
            // Interrupting a fade begins from its visible result, not an unmixed source frame.
            sprite.SetBehaviorState(PetState.Happy,null,direction);
            checks[$"transition_interruption_continuous_{direction}"]=Near(middle,RenderSprite(sprite));
            sprite.Advance(.3);
            playback=new WalkingPlayback(timing,4); playback.Begin(3,3);
            sprite.SetBehaviorState(PetState.Walking,playback,direction); sprite.Advance(.3);
            playback.Advance(playback.FinishSeconds-1/timing.Fps);
            var end=RenderSprite(sprite);
            sprite.SetBehaviorState(PetState.Idle,null,direction);
            checks[$"transition_idle_entry_{direction}"]=sprite.CurrentFrameIndex==9;
            checks[$"transition_return_frozen_outro_{direction}"]=Near(end,RenderSprite(sprite));
            sprite.Advance(.1); var returning=RenderSprite(sprite);
            checks[$"transition_return_alpha_and_hit_{direction}"]=RenderHasMargin(returning)&&AlphaMatchesRender(sprite,returning);
            SaveBitmap(returning,Path.Combine(_store.DirectoryPath,$"transition-walk-to-idle-{direction}.png"));
            sprite.Advance(.1);
            checks[$"transition_finishes_cleanly_{direction}"]=!sprite.TransitionActive&&sprite.CurrentFrameIndex==13;
            sprite.SetBehaviorState(PetState.Happy,null,direction); sprite.Advance(.05);
            var beforeDrag=RenderSprite(sprite);
            sprite.SetBehaviorState(PetState.Dragging,null,direction);
            checks[$"drag_immediately_takes_control_{direction}"]=sprite.State==PetState.Dragging
                &&sprite.TransitionActive&&Near(beforeDrag,RenderSprite(sprite));
        }
        foreach (var action in sprite.AvailableActions)
        {
            sprite.EndPreview(); sprite.SetBehaviorState(PetState.Idle,null,1); sprite.ResetAnimation();
            var before=RenderSprite(sprite);
            WalkingPlayback? walk=null;
            if (action.Key==nameof(PetState.Walking)) { walk=new(timing,4); walk.Begin(3,3); }
            sprite.BeginPreview(action.Key,walk,1);
            checks["preview_transition_starts_continuously_"+action.Key]=sprite.TransitionActive&&Near(before,RenderSprite(sprite));
            sprite.Advance(.1); var middle=RenderSprite(sprite);
            checks["preview_transition_alpha_"+action.Key]=RenderHasMargin(middle)&&AlphaMatchesRender(sprite,middle);
            sprite.EndPreview(PetState.Idle,null,1);
            checks["preview_return_starts_continuously_"+action.Key]=sprite.TransitionActive&&Near(middle,RenderSprite(sprite));
            sprite.Advance(.2);
            checks["preview_return_settles_"+action.Key]=!sprite.TransitionActive;
        }
        sprite.BeginPreview(nameof(PetState.Happy)); sprite.Advance(.35);
        var beforeRestart=RenderSprite(sprite); sprite.RestartAnimation();
        checks["repeated_action_restart_is_continuous"]=sprite.CurrentFrameIndex==0&&sprite.TransitionActive
            &&Near(beforeRestart,RenderSprite(sprite));
        sprite.EndPreview();
        var sequence=new WalkingPlayback(timing,4); sequence.Begin(3,3);
        sprite.SetBehaviorState(PetState.Walking,sequence,1); sprite.ResetAnimation();
        foreach (var position in new[]{timing.StartFrames/timing.Fps, sequence.FinishSeconds-timing.EndFrames/timing.Fps})
        {
            sequence.Advance(position-sequence.ElapsedSeconds-1/timing.Fps); sprite.Advance(.3);
            var last=RenderSprite(sprite); sprite.PrepareWalkingAdvance(1/timing.Fps);
            checks["walking_segment_blends_"+sequence.ClipKey]=sprite.TransitionActive&&Near(last,RenderSprite(sprite));
            sequence.Advance(1/timing.Fps); sprite.Advance(.3);
        }
        sequence=new WalkingPlayback(timing,4); sequence.Begin(3,3); sequence.Advance(timing.StartFrames/timing.Fps+.25);
        sprite.SetBehaviorState(PetState.Walking,sequence,1); sprite.ResetAnimation();
        var beforeTurn=RenderSprite(sprite); var turnFrame=sprite.CurrentFrameIndex;
        sprite.Direction=-1;
        checks["facing_turn_is_continuous"]=sprite.TransitionActive&&Near(beforeTurn,RenderSprite(sprite))
            &&sprite.CurrentFrameIndex==turnFrame;
        sprite.Advance(.2);
        checks["facing_turn_settles_without_restarting_walk"]=!sprite.TransitionActive&&!Near(beforeTurn,RenderSprite(sprite))
            &&sprite.CurrentFrameIndex==turnFrame;
        RunEdgeTurnChecks(checks);
    }

    private void RunEdgeTurnChecks(Dictionary<string,bool> checks)
    {
        var original=_settings;
        ApplySettings(original with { AutoWalk=false, Paused=false }); _tick.Stop();
        if (_sprite.WalkingTiming is not { } timing) return;
        foreach (var edge in new[]{-1,1})
        foreach (var automatic in new[]{true,false})
        {
            StopPreview(); _behavior.BeginDrag(); _behavior.EndDrag();
            if (_behavior.Direction!=edge) _behavior.Turn();
            WalkingPlayback walk;
            if (automatic)
            {
                _behavior.React(PetState.Walking); walk=_behavior.WalkingPlayback!;
                _sprite.SetBehaviorState(PetState.Walking,walk,edge);
            }
            else { PreviewAnimation(nameof(PetState.Walking)); walk=_walkingPreview!; _sprite.Direction=edge; }
            walk.Advance(timing.StartFrames/timing.Fps+.25); _sprite.Advance(.3);
            var area=CharacterWorkArea(); var rect=NativeDesktop.WindowRect(_handle);
            MoveClamped(area.X+edge*99999,rect.Y,area);
            var time=walk.ElapsedSeconds; AdvanceFrame(.25);
            checks[$"edge_turn_immediate_{automatic}_{edge}"]=_sprite.Direction==-edge
                &&(!automatic||_behavior.Direction==-edge)&&walk.ElapsedSeconds>time&&walk.Phase==WalkPhase.Loop;
            var atEdge=NativeDesktop.WindowRect(_handle); AdvanceFrame(.25);
            var inward=NativeDesktop.WindowRect(_handle); var bounds=CharacterScreenBounds();
            checks[$"edge_turn_moves_inward_{automatic}_{edge}"]=(inward.X-atEdge.X)*-edge>0
                &&bounds.X>=area.X-1&&bounds.Right<=area.Right+1;
        }
        StopPreview(); _behavior.BeginDrag(); _behavior.EndDrag(); ApplySettings(original); _tick.Stop();
    }
}
