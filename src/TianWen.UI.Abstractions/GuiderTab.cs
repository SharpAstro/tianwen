using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic guider tab. Shows guide error graph (RA/Dec polylines),
    /// RMS stats panel, and placeholder states when not guiding.
    /// </summary>
    public class GuiderTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Layout constants (at 1x scale)
        private const float BaseFontSize = 14f;
        private const float BasePadding = 8f;
        private const float BaseHeaderHeight = 32f;
        private const float BaseStatsWidth = 220f;
        private const float BaseCameraFraction = 0.4f; // guide camera gets 40% of width

        // Colors
        private static readonly RGBAColor32 ContentBg = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x66, 0x66, 0x88, 0xff);

        public GuiderTabState State { get; } = new GuiderTabState();

        /// <summary>Optional mini viewer widget for the guide camera image. Set by the host.</summary>
        public IMiniViewerWidget? GuideCameraViewer { get; set; }

        /// <summary>Tracks which guide frame reference is displayed to avoid redundant uploads.</summary>
        private Image? _displayedGuideFrame;
        private int _guideFrameCount;

        public override bool HandleInput(InputEvent evt) => false;

        public void Render(
            LiveSessionState liveState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            TimeProvider timeProvider)
        {
            BeginFrame();
            State.PollFromLiveState(liveState);

            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var headerH = BaseHeaderHeight * dpiScale;
            var statsW = BaseStatsWidth * dpiScale;

            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Header strip
            var headerRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, headerH);
            FillRect(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, HeaderBg);

            var placeholder = State.PlaceholderReason;
            if (placeholder is { } reason)
            {
                DrawText(GuiderActions.PlaceholderText(reason), fontPath,
                    headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                    fontSize, PlaceholderText, TextAlign.Near, TextAlign.Center);

                // Large centered placeholder
                var bodyY = contentRect.Y + headerH;
                var bodyH = contentRect.Height - headerH;
                DrawText(GuiderActions.PlaceholderText(reason), fontPath,
                    contentRect.X, bodyY, contentRect.Width, bodyH,
                    fontSize * 1.5f, PlaceholderText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Header: guider state + RMS
            var guiderLabel = State.GuiderState ?? "Guiding";
            DrawText($"[{guiderLabel}]", fontPath,
                headerRect.X + padding, headerRect.Y, 200 * dpiScale, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
            DrawText(GuiderActions.FormatRmsSummary(State.LastGuideStats), fontPath,
                headerRect.X + padding + 200 * dpiScale, headerRect.Y,
                headerRect.Width - 200 * dpiScale - padding * 2, headerRect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Far, TextAlign.Center);

            // Layout:
            // ┌─────────────────┬──────────┬────────┐
            // │  Guide Camera   │ Profile  │ Stats  │  top half of right panel
            // │  (large left)   ├──────────┴────────┤
            // │                 │   Target View      │  bottom half of right panel
            // └─────────────────┴───────────────────┘
            // │        Guide Error Graph             │
            // └──────────────────────────────────────┘
            var bodyTop = contentRect.Y + headerH;
            var bodyHeight = contentRect.Height - headerH;
            var graphH = Math.Max(bodyHeight * 0.2f, 80f * dpiScale);
            var mainH = bodyHeight - graphH;
            var rightW = statsW + 120f * dpiScale; // profile + stats width
            var cameraW = contentRect.Width - rightW;
            var rightX = contentRect.X + cameraW;

            // Right panel: top half = profile + stats, bottom half = target view
            var rightTopH = mainH * 0.5f;
            var rightBotH = mainH - rightTopH;
            var profileW = rightW - statsW;

            var cameraRect = new RectF32(contentRect.X, bodyTop, cameraW, mainH);
            var profileRect = new RectF32(rightX, bodyTop, profileW, rightTopH);
            var statsRect = new RectF32(rightX + profileW, bodyTop, statsW, rightTopH);
            var targetRect = new RectF32(rightX, bodyTop + rightTopH, rightW, rightBotH);
            var graphRect = new RectF32(contentRect.X, bodyTop + mainH, contentRect.Width, graphH);

            RenderGuideCamera(cameraRect, dpiScale, fontPath, fontSize);
            RenderStarProfile(profileRect, dpiScale, fontPath, fontSize);
            RenderStats(statsRect, dpiScale, fontPath, fontSize, padding);
            RenderTargetView(targetRect, dpiScale, fontPath, fontSize);
            RenderGraph(graphRect, dpiScale, fontPath, fontSize);
        }

        private static readonly RGBAColor32 CrosshairColor = new RGBAColor32(0x00, 0xff, 0x00, 0xaa);
        private static readonly RGBAColor32 CameraBg = new RGBAColor32(0x0a, 0x0a, 0x0a, 0xff);

        private void RenderGuideCamera(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, CameraBg);

            var image = State.LastGuideFrame;

            // Queue new guide frame to the mini viewer if changed
            if (GuideCameraViewer is { } viewer)
            {
                if (image is not null && !ReferenceEquals(image, _displayedGuideFrame))
                {
                    _displayedGuideFrame = image;
                    _guideFrameCount++;
                    viewer.QueueImage(image);
                }

                if (viewer.HasImage)
                {
                    viewer.Render(rect, Renderer.Width, Renderer.Height);

                    // Crosshair overlay on guide star position
                    if (State.GuideStarPosition is var (starX, starY) && image is not null)
                    {
                        var imgW = image.Width;
                        var imgH = image.Height;
                        var fitScale = Math.Min(rect.Width / imgW, rect.Height / imgH);
                        var drawW = imgW * fitScale;
                        var drawH = imgH * fitScale;
                        var offsetX = rect.X + (rect.Width - drawW) / 2;
                        var offsetY = rect.Y + (rect.Height - drawH) / 2;

                        var cx = (int)(offsetX + starX * fitScale);
                        var cy = (int)(offsetY + starY * fitScale);
                        var crossLen = (int)(15 * dpiScale);
                        var crossGap = (int)(4 * dpiScale);

                        FillRect(cx - crossLen, cy, crossLen - crossGap, 1, CrosshairColor);
                        FillRect(cx + crossGap, cy, crossLen - crossGap, 1, CrosshairColor);
                        FillRect(cx, cy - crossLen, 1, crossLen - crossGap, CrosshairColor);
                        FillRect(cx, cy + crossGap, 1, crossLen - crossGap, CrosshairColor);
                    }

                    // SNR + frame count in corner
                    var infoText = State.GuideStarSNR is { } snr
                        ? $"SNR: {snr:F0}  #{_guideFrameCount}"
                        : $"#{_guideFrameCount}";
                    DrawText(infoText, fontPath,
                        rect.X + 4 * dpiScale, rect.Y + rect.Height - fontSize * 1.4f,
                        200 * dpiScale, fontSize * 1.2f,
                        fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Far);

                    return;
                }
            }

            // Fallback: no viewer or no image
            DrawText(State.IsRunning ? "Waiting for guide frame\u2026" : "No guide camera",
                fontPath, rect.X, rect.Y, rect.Width, rect.Height,
                fontSize, DimText, TextAlign.Center, TextAlign.Center);
        }

        private static readonly RGBAColor32 ProfileBg = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
        private static readonly RGBAColor32 ProfileLineColor = new RGBAColor32(0x44, 0x99, 0x44, 0x88);
        private static readonly RGBAColor32 ProfileVLineColor = new RGBAColor32(0x44, 0x88, 0x99, 0x88);
        private static readonly RGBAColor32 ProfileFitColor = new RGBAColor32(0x66, 0xff, 0x66, 0xff);
        private static readonly RGBAColor32 ProfileVFitColor = new RGBAColor32(0x66, 0xdd, 0xff, 0xff);

        /// <summary>
        /// Star profile: 1D intensity cross-section through the guide star center.
        /// Shows horizontal (green) and vertical (cyan) profiles overlaid.
        /// </summary>
        private void RenderStarProfile(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, ProfileBg);

            var padding = BasePadding * dpiScale;
            var headerH = fontSize * 1.4f;

            DrawText("Star Profile", fontPath,
                rect.X + padding, rect.Y + padding,
                rect.Width - padding * 2, headerH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Near);

            if (State.GuideStarProfile is not var (hProfile, vProfile))
            {
                DrawText("Awaiting data\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var plotX = rect.X + padding;
            var plotY = rect.Y + padding + headerH;
            var plotW = rect.Width - padding * 2;
            var plotH = rect.Height - padding * 2 - headerH;

            if (plotW < 10 || plotH < 10) return;

            // Find max value across both profiles for shared Y scale
            var maxVal = 1f;
            for (var i = 0; i < hProfile.Length; i++)
            {
                if (hProfile[i] > maxVal) maxVal = hProfile[i];
            }
            for (var i = 0; i < vProfile.Length; i++)
            {
                if (vProfile[i] > maxVal) maxVal = vProfile[i];
            }

            // Draw raw profiles as step-style line charts
            var lineW = Math.Max(1f, dpiScale);
            DrawProfileLine(hProfile, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileLineColor);
            DrawProfileLine(vProfile, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileVLineColor);

            // Gaussian fit overlay (moment estimation — no iterative solver)
            var hFit = FitGaussian(hProfile);
            var vFit = FitGaussian(vProfile);

            if (hFit is var (hA, hMu, hSigma))
            {
                DrawGaussianCurve(hA, hMu, hSigma, hProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileFitColor);
            }
            if (vFit is var (vA, vMu, vSigma))
            {
                DrawGaussianCurve(vA, vMu, vSigma, vProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileVFitColor);
            }

            // FWHM text
            var fwhmText = "";
            if (hFit is var (_, _, hs)) fwhmText += $"H:{2.355 * hs:F1}px";
            if (vFit is var (_, _, vs)) fwhmText += (fwhmText.Length > 0 ? "  " : "") + $"V:{2.355 * vs:F1}px";
            if (fwhmText.Length > 0)
            {
                DrawText(fwhmText, fontPath,
                    rect.X + padding, rect.Y + padding + headerH,
                    plotW, fontSize,
                    fontSize * 0.75f, BodyText, TextAlign.Far, TextAlign.Near);
            }

            // Legend
            var legendY = rect.Y + rect.Height - padding - fontSize;
            FillRect((int)(rect.X + padding), (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), ProfileLineColor);
            DrawText("H", fontPath, rect.X + padding + 8 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
                fontSize * 0.7f, ProfileLineColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(rect.X + padding + 25 * dpiScale), (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), ProfileVLineColor);
            DrawText("V", fontPath, rect.X + padding + 33 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
                fontSize * 0.7f, ProfileVLineColor, TextAlign.Near, TextAlign.Center);
        }

        private void DrawProfileLine(float[] profile, float plotX, float plotY, float plotW, float plotH,
            float maxVal, float lineW, RGBAColor32 color)
        {
            if (profile.Length < 2) return;

            var step = plotW / (profile.Length - 1);
            for (var i = 1; i < profile.Length; i++)
            {
                var x1 = plotX + (i - 1) * step;
                var x2 = plotX + i * step;
                var y1 = plotY + plotH - (profile[i - 1] / maxVal * plotH);
                var y2 = plotY + plotH - (profile[i] / maxVal * plotH);

                // Horizontal segment then vertical connector (step-style)
                FillRect(x1, y1, x2 - x1, lineW, color);
                FillRect(x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
            }
        }

        /// <summary>
        /// Fits a Gaussian to a 1D profile via moment estimation (no iteration).
        /// Returns (amplitude, center, sigma) or default if the profile is flat.
        /// </summary>
        private static (float A, float Mu, float Sigma) FitGaussian(float[] profile)
        {
            var sumI = 0.0;
            var sumIX = 0.0;
            var peak = 0f;

            for (var i = 0; i < profile.Length; i++)
            {
                var v = profile[i];
                sumI += v;
                sumIX += v * i;
                if (v > peak) peak = v;
            }

            if (sumI <= 0 || peak <= 0)
            {
                return default;
            }

            var mu = sumIX / sumI;

            var sumIXX = 0.0;
            for (var i = 0; i < profile.Length; i++)
            {
                var d = i - mu;
                sumIXX += profile[i] * d * d;
            }

            var sigma = Math.Sqrt(sumIXX / sumI);
            if (sigma < 0.5) sigma = 0.5; // minimum width

            return ((float)peak, (float)mu, (float)sigma);
        }

        private void DrawGaussianCurve(float amplitude, float mu, float sigma, int profileLen,
            float plotX, float plotY, float plotW, float plotH, float maxVal, float lineW, RGBAColor32 color)
        {
            var steps = (int)plotW;
            if (steps < 2) return;

            var twoSigmaSq = 2.0 * sigma * sigma;
            for (var i = 1; i < steps; i++)
            {
                var t0 = (float)(i - 1) / steps * (profileLen - 1);
                var t1 = (float)i / steps * (profileLen - 1);
                var g0 = amplitude * Math.Exp(-((t0 - mu) * (t0 - mu)) / twoSigmaSq);
                var g1 = amplitude * Math.Exp(-((t1 - mu) * (t1 - mu)) / twoSigmaSq);

                var x1 = plotX + (float)(i - 1) / steps * plotW;
                var x2 = plotX + (float)i / steps * plotW;
                var y1 = plotY + plotH - (float)(g0 / maxVal) * plotH;
                var y2 = plotY + plotH - (float)(g1 / maxVal) * plotH;

                FillRect(x1, y1, x2 - x1, lineW, color);
                FillRect(x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
            }
        }

        private static readonly RGBAColor32 TargetBg = new RGBAColor32(0x10, 0x10, 0x18, 0xff);
        private static readonly RGBAColor32 TargetRingColor = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 RmsRingColor = new RGBAColor32(0x44, 0x66, 0x44, 0xff);
        private static readonly RGBAColor32 RecentDotColor = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 OldDotColor = new RGBAColor32(0x66, 0x66, 0x88, 0x88);

        /// <summary>
        /// PHD2-style target view: 2D scatter of RA (X) vs Dec (Y) error with RMS circle.
        /// </summary>
        private void RenderTargetView(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TargetBg);

            var samples = State.GuideSamples;
            if (samples.Length < 2)
            {
                DrawText("Target View", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var padding = BasePadding * dpiScale;
            var side = Math.Min(rect.Width, rect.Height) - padding * 2;
            var cx = rect.X + rect.Width / 2;
            var cy = rect.Y + rect.Height / 2;
            var halfSide = side / 2;

            // Fixed scale: rings at 3", 6", 9", 12" (outer ring = 12")
            const double targetScaleArcsec = 12.0;
            const double ringStepArcsec = 3.0;

            // Concentric rings at fixed arcsec intervals
            for (var ring = 1; ring <= 4; ring++)
            {
                var r = (float)(ring * ringStepArcsec / targetScaleArcsec * halfSide);
                DrawRing(cx, cy, r, ring == 4 ? GuideGraphRenderer.ZeroLineColor : TargetRingColor);
            }

            // Crosshair — short marks at center only
            var crossLen = 8f * dpiScale;
            FillRect(cx - crossLen, cy, crossLen * 2, 1, TargetRingColor);
            FillRect(cx, cy - crossLen, 1, crossLen * 2, TargetRingColor);

            // Axis labels
            var labelSize = fontSize * 0.7f;
            DrawText("RA", fontPath, rect.X + rect.Width - padding - 20 * dpiScale, cy + 2, 20 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.RaColor, TextAlign.Far, TextAlign.Near);
            DrawText("Dec", fontPath, cx + 2, rect.Y + padding, 30 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Near);

            // Scale label
            DrawText($"\u00b1{targetScaleArcsec:F0}\"", fontPath,
                rect.X + padding, rect.Y + rect.Height - labelSize * 1.5f, 50 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

            // RMS circle
            if (State.LastGuideStats is { TotalRMS: > 0 } stats)
            {
                var rmsR = (float)(stats.TotalRMS / targetScaleArcsec * halfSide);
                if (rmsR > 2)
                {
                    DrawRing(cx, cy, Math.Min(rmsR, halfSide), RmsRingColor);
                }
            }

            // Plot dots — recent samples brighter, older samples dimmer
            var recentCount = Math.Min(samples.Length, 50);
            var startIdx = samples.Length - recentCount;

            for (var i = startIdx; i < samples.Length; i++)
            {
                var s = samples[i];
                var px = cx + (float)(Math.Clamp(s.RaError / targetScaleArcsec, -1, 1) * halfSide);
                var py = cy - (float)(Math.Clamp(s.DecError / targetScaleArcsec, -1, 1) * halfSide);

                // Fade: newest = white, oldest = dim
                var age = (float)(i - startIdx) / recentCount;
                var dotColor = age > 0.8f ? RecentDotColor : OldDotColor;
                var dotSize = age > 0.8f ? 3 : 2;

                FillRect((int)px - dotSize / 2, (int)py - dotSize / 2, dotSize, dotSize, dotColor);
            }

            // Latest point as larger bright dot
            if (samples.Length > 0)
            {
                var last = samples[^1];
                var lx = cx + (float)(Math.Clamp(last.RaError / targetScaleArcsec, -1, 1) * halfSide);
                var ly = cy - (float)(Math.Clamp(last.DecError / targetScaleArcsec, -1, 1) * halfSide);
                FillRect((int)lx - 2, (int)ly - 2, 5, 5, CrosshairColor);
            }
        }

        /// <summary>
        /// Draws an approximate circle using horizontal line segments.
        /// </summary>
        private void DrawRing(float cx, float cy, float radius, RGBAColor32 color)
        {
            var steps = Math.Max(32, (int)(radius * 2));
            for (var i = 0; i < steps; i++)
            {
                var angle = 2.0 * Math.PI * i / steps;
                var px = (int)(cx + radius * Math.Cos(angle));
                var py = (int)(cy + radius * Math.Sin(angle));
                FillRect(px, py, 1, 1, color);
            }
        }

        private void RenderGraph(RectF32 rect, float dpiScale, string fontPath, float fontSize)
        {
            var samples = State.GuideSamples;
            if (samples.Length < 2)
            {
                FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);
                DrawText("Waiting for guide data\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuideGraphRenderer.GraphBg);

            var yScale = GuideGraphRenderer.ComputeYScale(State.LastGuideStats);
            var padding = BasePadding * dpiScale;
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);

            // Y-axis labels
            var labelW = 40f * dpiScale;
            DrawText($"+{yScale:F0}\"", fontPath,
                rect.X, rect.Y, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Near);
            DrawText("0\"", fontPath,
                rect.X, zeroY - fontSize * 0.5f, labelW, fontSize,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Center);
            DrawText($"-{yScale:F0}\"", fontPath,
                rect.X, rect.Y + rect.Height - fontSize * 1.2f, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

            // Connected step-style lines
            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var lineW = Math.Max(dpiScale, 1f);

            for (var i = 1; i < visibleCount; i++)
            {
                var x1 = rect.X + (i - 1) * spacing;
                var x2 = rect.X + i * spacing;

                var raY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].RaError, yScale, zeroY, halfH);
                var raY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].RaError, yScale, zeroY, halfH);
                FillRect(x1, raY1, x2 - x1, lineW, GuideGraphRenderer.RaColor);
                FillRect(x2, Math.Min(raY1, raY2), lineW, Math.Abs(raY2 - raY1) + lineW, GuideGraphRenderer.RaColor);

                var decY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].DecError, yScale, zeroY, halfH);
                var decY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].DecError, yScale, zeroY, halfH);
                FillRect(x1, decY1, x2 - x1, lineW, GuideGraphRenderer.DecColor);
                FillRect(x2, Math.Min(decY1, decY2), lineW, Math.Abs(decY2 - decY1) + lineW, GuideGraphRenderer.DecColor);
            }

            // Legend
            var legendY = rect.Y + rect.Height - padding * 2;
            FillRect((int)(rect.X + padding), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.RaColor);
            DrawText("RA", fontPath,
                rect.X + padding + 10 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.RaColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(rect.X + padding + 50 * dpiScale), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.DecColor);
            DrawText("Dec", fontPath,
                rect.X + padding + 60 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Center);
        }

        private void RenderStats(RectF32 rect, float dpiScale, string fontPath, float fontSize, float padding)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            var stats = State.LastGuideStats;
            var cursor = rect.Y + padding;
            var lineH = fontSize * 1.6f;
            var labelW = 90f * dpiScale;
            var valueX = rect.X + padding + labelW;
            var valueW = rect.Width - padding * 2 - labelW;

            // Header
            DrawText("Guide Stats", fontPath,
                rect.X + padding, cursor, rect.Width - padding * 2, lineH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
            cursor += lineH * 1.2f;

            if (stats is null)
            {
                DrawText("No data", fontPath,
                    rect.X + padding, cursor, rect.Width - padding * 2, lineH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            // Stats rows
            void DrawRow(string label, string value, RGBAColor32? valueColor = null)
            {
                DrawText(label, fontPath, rect.X + padding, cursor, labelW, lineH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(value, fontPath, valueX, cursor, valueW, lineH,
                    fontSize * 0.9f, valueColor ?? BodyText, TextAlign.Near, TextAlign.Center);
                cursor += lineH;
            }

            DrawRow("Total RMS:", $"{stats.TotalRMS:F2}\"");
            DrawRow("RA RMS:", $"{stats.RaRMS:F2}\"", GuideGraphRenderer.RaColor);
            DrawRow("Dec RMS:", $"{stats.DecRMS:F2}\"", GuideGraphRenderer.DecColor);
            cursor += lineH * 0.3f;
            DrawRow("Peak RA:", $"{stats.PeakRa:F2}\"");
            DrawRow("Peak Dec:", $"{stats.PeakDec:F2}\"");
            cursor += lineH * 0.3f;

            if (stats.LastRaErr.HasValue)
            {
                DrawRow("Last RA:", $"{stats.LastRaErr.Value:+0.00;-0.00}\"", GuideGraphRenderer.RaColor);
                DrawRow("Last Dec:", $"{stats.LastDecErr ?? 0:+0.00;-0.00}\"", GuideGraphRenderer.DecColor);
                cursor += lineH * 0.3f;
            }

            if (State.GuideExposure > TimeSpan.Zero)
            {
                DrawRow("Exposure:", $"{State.GuideExposure.TotalSeconds:F1}s");
            }

            var settle = State.GuiderSettleProgress;
            if (settle is { Done: false })
            {
                DrawRow("Settle:", $"{settle.Distance:F2}\" / {settle.SettlePx:F2}\"");
            }
        }
    }
}
