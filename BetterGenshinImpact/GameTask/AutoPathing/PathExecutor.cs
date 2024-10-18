﻿using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Map;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class PathExecutor(CancellationTokenSource cts)
{
    private readonly CameraRotateTask _rotateTask = new(cts);
    private readonly TrapEscaper _trapEscaper = new(cts);
    
    public async Task Pathing(PathingTask task)
    {
        if (!task.Positions.Any())
        {
            Logger.LogWarning("没有路径点，寻路结束");
            return;
        }

        InitializePathing(task);

        var waypoints = ConvertWaypointsForTrack(task.Positions);

        await Delay(100, cts);
        Navigation.WarmUp(); // 提前加载地图特征点

        try
        {
            foreach (var waypoint in waypoints)
            {
                if (waypoint.Type == WaypointType.Teleport.Code)
                {
                    await HandleTeleportWaypoint(waypoint);
                }
                else
                {
                    // Path不用走得很近，Target需要接近，但都需要先移动到对应位置
                    await MoveTo(waypoint);

                    if (waypoint.Type == WaypointType.Target.Code || !string.IsNullOrEmpty(waypoint.Action))
                    {
                        await MoveCloseTo(waypoint);
                        // 到达点位后执行 action
                        await AfterMoveToTarget(waypoint);
                    }
                }
            }
        }
        finally
        {
            // 不管咋样，松开所有按键
            Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
            Simulation.SendInput.Mouse.RightButtonUp();
        }
    }

    private void InitializePathing(PathingTask task)
    {
        WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(this,
            "UpdateCurrentPathing", new object(), task));
    }

    private List<WaypointForTrack> ConvertWaypointsForTrack(List<Waypoint> positions)
    {
        // 把 X Y 转换为 MatX MatY
        return positions.Select(waypoint => new WaypointForTrack(waypoint)).ToList();
    }

    private async Task HandleTeleportWaypoint(WaypointForTrack waypoint)
    {
        var forceTp = waypoint.Action == ActionEnum.ForceTp.Code;
        var (tpX, tpY) = await new TpTask(cts).Tp(waypoint.GameX, waypoint.GameY, forceTp);
        var (tprX, tprY) = MapCoordinate.GameToMain2048(tpX, tpY);
        EntireMap.Instance.SetPrevPosition((float)tprX, (float)tprY); // 通过上一个位置直接进行局部特征匹配
    }

    private async Task MoveTo(WaypointForTrack waypoint)
    {
        var screen = CaptureToRectArea();
        var position = Navigation.GetPosition(screen);
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogInformation("粗略接近途经点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await _rotateTask.WaitUntilRotatedTo(targetOrientation, 5);
        var startTime = DateTime.UtcNow;
        var lastPositionRecord = DateTime.UtcNow;
        var fastMode = false;
        var prevPositions = new List<Point2f>(); 
        var fastmodeColdTime = DateTime.UtcNow;
        bool isTrapped = false;
        
        // 按下w，一直走
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W);
        while (!cts.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if ((now - startTime).TotalSeconds > 240)
            {
                Logger.LogWarning("执行超时，跳过路径点");
                break;
            }

            screen = CaptureToRectArea();
            position = Navigation.GetPosition(screen);
            var distance = Navigation.GetDistance(waypoint, position);
            Debug.WriteLine($"接近目标点中，距离为{distance}");
            if (distance < 4)
            {
                Logger.LogInformation("到达路径点附近");
                break;
            }

            if (distance > 500)
            {
                Logger.LogWarning("距离过远，跳过路径点");
                break;
            }

            // 非攀爬状态下，检测是否卡死（脱困触发器）
            if (waypoint.MoveMode != MoveModeEnum.Climb.Code)
            {   
                if ((now - lastPositionRecord).TotalMilliseconds > 1000)
                {
                    lastPositionRecord = now;
                    prevPositions.Add(position);
                    if (prevPositions.Count > 8)
                    {
                        var delta = prevPositions[^1] - prevPositions[^8];
                        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 3)
                        {
                            Logger.LogWarning("疑似卡死，尝试脱离");
                            
                            //调用脱困代码，由TrapEscaper接管移动
                            await _trapEscaper.RotateAndMove();
                            await _trapEscaper.MoveTo(waypoint);
                            Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W);
                            continue;
                        }
                    }
                }
            }

            // 旋转视角
            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            //执行旋转
            _rotateTask.RotateToApproach(targetOrientation, screen);
            
            // 根据指定方式进行移动
            if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
            {
                var isFlying = Bv.GetMotionStatus(screen) == MotionStatus.Fly;
                if (!isFlying)
                {
                    Debug.WriteLine("未进入飞行状态，按下空格");
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_SPACE);
                    await Delay(200, cts);
                }
                continue;
            }
            if (waypoint.MoveMode == MoveModeEnum.Climb.Code)
            {
                if (Bv.GetMotionStatus(screen) != MotionStatus.Climb)
                {
                    Debug.WriteLine("未进入攀爬状态，按下空格");
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_SPACE);
                    await Delay(200, cts);
                }
                continue;
            }
            if (waypoint.MoveMode == MoveModeEnum.Jump.Code)
            {
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_SPACE);
                await Delay(200, cts);
                continue;
            }
            // 设置为非攀爬时误进入攀爬，自动脱离（小脱困）
            // 小脱困逻辑，在进入攀爬时，即后一帧会自动脱离，后使用TrapEscaper接管移动

            // 先排除攀爬和飞行的情况
            if (waypoint.MoveMode != MoveModeEnum.Climb.Code &&
                waypoint.MoveMode != MoveModeEnum.Fly.Code)
                if (Bv.GetMotionStatus(screen) == MotionStatus.Climb)
                {
                    Logger.LogWarning("进入攀爬状态，自动脱困中");
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_X);
                    await _trapEscaper.MoveTo(waypoint);
                    Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W);
                    continue;
                }

            // 只有设置为run才会一直疾跑
            if (waypoint.MoveMode == MoveModeEnum.Run.Code)
            {
                if (distance > 20 != fastMode) // 距离大于30时可以使用疾跑/自由泳
                {
                    if (fastMode)
                    {
                        Simulation.SendInput.Mouse.RightButtonUp();
                    }
                    else
                    {
                        Simulation.SendInput.Mouse.RightButtonDown();
                    }

                    fastMode = !fastMode;
                }
            }
            else if (waypoint.MoveMode != MoveModeEnum.Climb.Code)//否则自动短疾跑
            {
                if (distance > 10)
                {
                    if (Math.Abs((fastmodeColdTime-now).TotalMilliseconds) > 2500) //冷却时间2.5s，回复体力用
                    {
                        fastmodeColdTime = now;
                        Simulation.SendInput.Mouse.RightButtonClick();
                    }
                }
            }

            await Delay(100, cts);
        }

        // 抬起w键
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
    }

    private async Task MoveCloseTo(WaypointForTrack waypoint)
    {
        var screen = CaptureToRectArea();
        var position = Navigation.GetPosition(screen);
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogInformation("精确接近目标点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        if (waypoint.MoveMode == MoveModeEnum.Fly.Code && waypoint.Action == ActionEnum.StopFlying.Code)
        {
            //下落攻击接近目的地
            Logger.LogInformation("动作：下落攻击");
            Simulation.SendInput.Mouse.LeftButtonClick();
            await Delay(1000, cts);
        }

        await _rotateTask.WaitUntilRotatedTo(targetOrientation, 2);
        var stepsTaken = 0;
        while (!cts.IsCancellationRequested)
        {
            stepsTaken++;
            if (stepsTaken > 20)
            {
                Logger.LogWarning("精确接近超时");
                break;
            }

            screen = CaptureToRectArea();
            position = Navigation.GetPosition(screen);
            if (Navigation.GetDistance(waypoint, position) < 2)
            {
                Logger.LogInformation("已到达路径点");
                break;
            }

            await _rotateTask.WaitUntilRotatedTo(targetOrientation, 2);
            // 小碎步接近
            Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W).Sleep(60).KeyUp(User32.VK.VK_W);
            await Delay(50, cts);
        }

        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);

        // 到达目的地后停顿一秒
        await Delay(1000, cts);
    }

    private async Task AfterMoveToTarget(Waypoint waypoint)
    {
        if (waypoint.Action == ActionEnum.NahidaCollect.Code
            || waypoint.Action == ActionEnum.PickAround.Code
            || waypoint.Action == ActionEnum.Fight.Code)
        {
            var handler = ActionFactory.GetHandler(waypoint.Action);
            await handler.RunAsync(cts);
            await Delay(800, cts);
        }
    }
}
