﻿using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoSkip.Model;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.ViewModel.Pages;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using WinRT;

namespace BetterGenshinImpact.GameTask.AutoSkip;

public class AutoTrackTask(AutoTrackParam param) : BaseIndependentTask
{
    // /// <summary>
    // /// 准备好前进了
    // /// </summary>
    // private bool _readyMoveForward = false;

    /// <summary>
    /// 任务距离
    /// </summary>
    private Rect _missionDistanceRect = Rect.Empty;

    public async void Start()
    {
        var hasLock = false;
        try
        {
            hasLock = await TaskSemaphore.WaitAsync(0);
            if (!hasLock)
            {
                Logger.LogError("启动自动追踪功能失败：当前存在正在运行中的独立任务，请不要重复执行任务！");
                return;
            }

            SystemControl.ActivateWindow();

            Logger.LogInformation("→ {Text}", "自动追踪，启动！");

            TrackMission();
        }
        catch (NormalEndException e)
        {
            Logger.LogInformation("自动追踪中断:" + e.Message);
            // NotificationHelper.SendTaskNotificationWithScreenshotUsing(b => b.Domain().Cancelled().Build());
        }
        catch (Exception e)
        {
            Logger.LogError(e.Message);
            Logger.LogDebug(e.StackTrace);
            // NotificationHelper.SendTaskNotificationWithScreenshotUsing(b => b.Domain().Failure().Build());
        }
        finally
        {
            VisionContext.Instance().DrawContent.ClearAll();
            TaskSettingsPageViewModel.SetSwitchAutoTrackButtonText(false);
            Logger.LogInformation("→ {Text}", "自动追踪结束");

            if (hasLock)
            {
                TaskSemaphore.Release();
            }
        }
    }

    private void TrackMission()
    {
        // 确认在主界面才会执行跟随任务
        var ra = GetRectAreaFromDispatcher();
        var paimonMenuRa = ra.Find(ElementAssets.Instance.PaimonMenuRo);
        if (!paimonMenuRa.IsExist())
        {
            Sleep(5000, param.Cts);
            return;
        }

        // 任务文字有动效，等待2s重新截图
        Simulation.SendInput.Mouse.MoveMouseBy(0, 7000);
        Sleep(2000, param.Cts);

        // OCR 任务文字 在小地图下方
        var textRaList = OcrMissionTextRaList(paimonMenuRa);
        if (textRaList.Count == 0)
        {
            Logger.LogInformation("未找到任务文字");
            Sleep(5000, param.Cts);
            return;
        }

        // 从任务文字中提取距离
        var distance = GetDistanceFromMissionText(textRaList);
        Logger.LogInformation("任务追踪：{Text}", "距离" + distance + "m");
        if (distance >= 150)
        {
            // 距离大于150米，先传送到最近的传送点
            // J 打开任务 切换追踪打开地图 中心点就是任务点
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_J);
            Sleep(800, param.Cts);
            // TODO 识别是否在任务界面
            // 切换追踪
            var btn = ra.Derive(CaptureRect.Width - 250, CaptureRect.Height - 60);
            btn.Click();
            Sleep(200, param.Cts);
            btn.Click();
            Sleep(1500, param.Cts);

            // 寻找所有传送点
            ra = GetRectAreaFromDispatcher();
            var tpPointList = MatchTemplateHelper.MatchMultiPicForOnePic(ra.SrcGreyMat, QuickTeleportAssets.Instance.MapChooseIconGreyMatList);
            if (tpPointList.Count > 0)
            {
                // 选中离中心点最近的传送点
                var centerX = ra.Width / 2;
                var centerY = ra.Height / 2;
                var minDistance = double.MaxValue;
                var nearestRect = Rect.Empty;
                foreach (var tpPoint in tpPointList)
                {
                    var distanceTp = Math.Sqrt(Math.Pow(Math.Abs(tpPoint.X - centerX), 2) + Math.Pow(Math.Abs(tpPoint.Y - centerY), 2));
                    if (distanceTp < minDistance)
                    {
                        minDistance = distanceTp;
                        nearestRect = tpPoint;
                    }
                }

                ra.Derive(nearestRect).Click();
                // 等待自动传送完成
                Sleep(2000, param.Cts);

                if (Bv.IsInBigMapUi(GetRectAreaFromDispatcher()))
                {
                    Logger.LogWarning("仍旧在大地图界面，传送失败");
                }
                else
                {
                    Sleep(500, param.Cts);
                    NewRetry.Do(() =>
                    {
                        if (!Bv.IsInMainUi(GetRectAreaFromDispatcher()))
                        {
                            Logger.LogInformation("未进入到主界面，继续等待");
                            throw new RetryException("未进入到主界面");
                        }
                    }, TimeSpan.FromSeconds(1), 100);
                    StartTrackPoint();
                }
            }
            else
            {
                Logger.LogWarning("未找到传送点");
            }
        }
        else
        {
            StartTrackPoint();
        }
    }

    private void StartTrackPoint()
    {
        // V键直接追踪
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_V);
        Sleep(3000, param.Cts);

        var ra = GetRectAreaFromDispatcher();
        var blueTrackPointRa = ra.Find(ElementAssets.Instance.BlueTrackPoint);
        if (blueTrackPointRa.IsExist())
        {
            MakeBlueTrackPointDirectlyAbove();
        }
        else
        {
            Logger.LogWarning("首次未找到追踪点");
        }
    }

    /// <summary>
    /// 找到追踪点并调整方向
    /// </summary>
    private void MakeBlueTrackPointDirectlyAbove()
    {
        // return new Task(() =>
        // {
        int prevMoveX = 0;
        bool wDown = false;
        while (!param.Cts.Token.IsCancellationRequested)
        {
            var ra = GetRectAreaFromDispatcher();
            var blueTrackPointRa = ra.Find(ElementAssets.Instance.BlueTrackPoint);
            if (blueTrackPointRa.IsExist())
            {
                // 使追踪点位于俯视角上方
                var centerY = blueTrackPointRa.Y + blueTrackPointRa.Height / 2;
                if (centerY > CaptureRect.Height / 2)
                {
                    Simulation.SendInput.Mouse.MoveMouseBy(-50, 0);
                    if (wDown)
                    {
                        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
                        wDown = false;
                    }
                    Debug.WriteLine("使追踪点位于俯视角上方");
                    continue;
                }

                // 调整方向
                var centerX = blueTrackPointRa.X + blueTrackPointRa.Width / 2;
                var moveX = (centerX - CaptureRect.Width / 2) / 8;
                moveX = moveX switch
                {
                    > 0 and < 10 => 10,
                    > -10 and < 0 => -10,
                    _ => moveX
                };
                if (moveX != 0)
                {
                    Simulation.SendInput.Mouse.MoveMouseBy(moveX, 0);
                    Debug.WriteLine("调整方向:" + moveX);
                }

                if (moveX == 0 || prevMoveX * moveX < 0)
                {
                    if (!wDown)
                    {
                        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W);
                        wDown = true;
                    }
                }

                if (Math.Abs(moveX) < 50 && Math.Abs(centerY - CaptureRect.Height / 2) < 200)
                {
                    if (wDown)
                    {
                        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
                        wDown = false;
                    }
                    // 识别距离
                    var text = OcrFactory.Paddle.OcrWithoutDetector(ra.SrcGreyMat[_missionDistanceRect]);
                    if (StringUtils.TryExtractPositiveInt(text) is > -1 and <= 3)
                    {
                        Logger.LogInformation("任务追踪：到达目标,识别结果[{Text}]", text);
                        break;
                    }
                    Logger.LogInformation("任务追踪：到达目标");
                    break;
                }

                prevMoveX = moveX;
            }
            else
            {
                // 随机移动
                Logger.LogInformation("未找到追踪点");
            }

            Simulation.SendInput.Mouse.MoveMouseBy(0, 500); // 保证俯视角
            Sleep(100);
        }
        // });
    }

    private int GetDistanceFromMissionText(List<Region> textRaList)
    {
        // 打印所有任务文字
        var text = textRaList.Aggregate(string.Empty, (current, textRa) => current + textRa.Text.Trim() + "|");
        Logger.LogInformation("任务追踪：{Text}", text);

        foreach (var textRa in textRaList)
        {
            if (textRa.Text.Length < 8 && (textRa.Text.Contains("m") || textRa.Text.Contains("M")))
            {
                _missionDistanceRect = textRa.ConvertSelfPositionToGameCaptureRegion();
                return StringUtils.TryExtractPositiveInt(textRa.Text);
            }
        }

        return -1;
    }

    private List<Region> OcrMissionTextRaList(Region paimonMenuRa)
    {
        return GetRectAreaFromDispatcher().FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = new Rect(paimonMenuRa.X, paimonMenuRa.Y - 15 + 210,
                (int)(300 * AssetScale), (int)(100 * AssetScale))
        });
    }
}
