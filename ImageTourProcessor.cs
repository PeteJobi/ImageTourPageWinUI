using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIShared.Controls;
using WinUIShared.Helpers;

namespace ImageTourPage
{
    public class ImageTourProcessor(string ffmpegPath): Processor(ffmpegPath)
    {
        private string folder;
        private string inputPath;
        private int width;
        private int height;
        private int totalWidth;
        private int totalHeight;
        private double fps;
        private int totalFrames;
        private int currentTransition;
        private Transition[] transitions;
        private int[] frameCountsPerTransition;
        private bool dontDeleteFrames;
        private bool isVideo;
        private bool isGeneratingFrames;
        private bool isPaused;
        private bool isCanceled;
        private CancellationTokenSource pauseTokenSource = new();

        public async Task Animate(string inputFileName, bool isVideo, int outputWidth, int outputHeight, double outputFps, IEnumerable<Transition> transitionSteps, bool useSingleRunMethod, bool dontDeleteGeneratedFrames = false)
        {
            inputPath = inputFileName;
            width = outputWidth;
            height = outputHeight;
            fps = outputFps;
            this.isVideo = isVideo;
            transitions = transitionSteps.ToArray();
            frameCountsPerTransition = new int[transitions.Length];
            dontDeleteFrames = dontDeleteGeneratedFrames;
            totalFrames = 0;
            isCanceled = false;

            if (!transitions.Any())
            {
                error("No transitions specified");
                return;
            }
            if (width * height == 0)
            {
                error("Both output dimensions should be greater than 0");
                return;
            }
            if (width % 2 != 0 || height % 2 != 0)
            {
                error("Both output dimensions should be divisible by 2");
                return;
            }
            if (!File.Exists(inputPath))
            {
                error("The input file could not be found");
                return;
            }

            await Setup(useSingleRunMethod);

            foreach (var transition in transitions)
            {
                if (!ValidateKeyFrame(transition.StartKeyFrame))
                {
                    error($"{transition.StartKeyFrame} exceeds the bounds of the input image");
                    return;
                }
                if (!ValidateKeyFrame(transition.EndKeyFrame))
                {
                    error($"{transition.EndKeyFrame} exceeds the bounds of the input image");
                    return;
                }
            }


            await (useSingleRunMethod ? SingleRunMethod() : FrameExtractionMethod());
            return;


            async Task FrameExtractionMethod()
            {
                var lastKeyFrame = new KeyFrame();
                var lastFrame = 0;
                var lastEndTime = TimeSpan.MinValue;

                try
                {
                    isGeneratingFrames = true;
                    rightTextPrimary.Report("Generating frames...");
                    foreach (var transition in transitions)
                    {
                        rightTextPrimary.Report($"{currentTransition} / {transitions.Length}");
                        var startNum = currentTransition * 2 - 1;
                        leftTextPrimary.Report($"{startNum} -> {startNum + 1}");

                        if (!Equals(transition.StartKeyFrame, lastKeyFrame) || (isVideo && transition.Start != lastEndTime))
                        {
                            lastFrame++;
                            await GenerateFrame(transition.StartKeyFrame, lastFrame, transition.Start); //Generate first frame
                            RecordFrameGenerationProgress(lastFrame, 1);
                            await CheckPause();
                            var canceled = CheckCanceled();
                            if (canceled) return;
                        }

                        var transitionFrameCount = await ProcessTransitionFrames(transition, lastFrame);
                        if (transitionFrameCount == -1)
                        {
                            CheckCanceled();
                            return;
                        }
                        lastFrame += transitionFrameCount;
                        lastKeyFrame = transition.EndKeyFrame;
                        lastEndTime = transition.End;
                        currentTransition++;
                    }
                    isGeneratingFrames = false;
                }
                catch (Exception e)
                {
                    CleanUp();
                    error($"An error occurred during frame generation: {e}");
                    return;
                }

                try
                {
                    leftTextPrimary.Report(string.Empty);
                    rightTextPrimary.Report("Merging frames...");

                    var audioArgs = isVideo ? $"-filter_complex \"{GetAudioComplexFilter(true)}\"  -map \"[outA]\"" : string.Empty;
                    var currGpu = gpuInfo;
                    DisableHardwareAccel(); //Disable hardware acceleration
                    await StartFfmpegTranscodingProcess([$"{folder}/frame%08d.png", inputPath], GetOutputName(inputPath), 18, null, $"-r {fps}", $"-map 0:v {audioArgs} -c:a aac -vf scale=out_color_matrix=bt709,format=yuv420p", (_, _, _, currentFrame) =>
                    {
                        RecordMergeProgress(currentFrame);
                    });
                    EnableHardwareAccelParams(currGpu);
                    if (HasBeenKilled())
                    {
                        ProcessCanceled();
                        return;
                    }
                }
                catch (Exception e)
                {
                    CleanUp();
                    error($"An error occurred during video creation: {e}");
                    return;
                }
                CleanUp();
            }

            async Task SingleRunMethod()
            {
                var gpuPixelFormat = isVideo ? await GetGpuPixelFormat(inputPath) : string.Empty;
                var vTrimScaleCropBuilder = new StringBuilder();
                var vConcatBuilder = new StringBuilder();

                for (var i = 0; i < transitions.Length; i++)
                {
                    var transition = transitions[i];
                    var trim =
                        $"start={TimeSpanString(transition.Start)}:end={TimeSpanString(transition.End)},setpts=PTS-STARTPTS";
                    var widthChange = transition.EndKeyFrame.Width - transition.StartKeyFrame.Width;
                    var heightChange = transition.EndKeyFrame.Height - transition.StartKeyFrame.Height;
                    var xChange = transition.EndKeyFrame.X - transition.StartKeyFrame.X;
                    var yChange = transition.EndKeyFrame.Y - transition.StartKeyFrame.Y;
                    var (w1, h1, x1, y1) =
                        (transition.StartKeyFrame.Width, transition.StartKeyFrame.Height, transition.StartKeyFrame.X,
                            transition.StartKeyFrame.Y);
                    var d = transition.Duration.TotalSeconds;
                    var rangeArg = isVideo ? string.Empty : ":out_range=tv";
                    var widthFactor = $"({outputWidth}/({w1}+{widthChange}*(t/{d})))";
                    var heightFactor = $"({outputHeight}/({h1}+{heightChange}*(t/{d})))";
                    var scale =
                        $"'iw*{widthFactor}':'ih*{heightFactor}':eval=frame:out_color_matrix=bt709{rangeArg}:flags=lanczos+accurate_rnd+full_chroma_int+full_chroma_inp";
                    var crop =
                        $"{outputWidth}:{outputHeight}:'min(max(0, ({x1}+{xChange}*(t/{d}))*{widthFactor}), (iw*{widthFactor})-{outputWidth})'" +
                        $":'min(max(0, ({y1}+{yChange}*(t/{d}))*{heightFactor}), (ih*{heightFactor})-{outputHeight})'" +
                        $":exact=1,scale={outputWidth}:{outputHeight}:flags=lanczos+accurate_rnd,setsar=1";
                    var (hwDownArgs, hwUpArgs) = isVideo ? GpuInfo.FilterParams(gpuInfo, gpuPixelFormat) : (string.Empty, string.Empty);
                    vTrimScaleCropBuilder.Append($"[0:v]{hwDownArgs}format=rgb24,fps={fps},trim={trim},scale={scale},crop={crop}{hwUpArgs}[v{i}];");
                    vConcatBuilder.Append($"[v{i}]");
                }

                var audioFilter = isVideo ? GetAudioComplexFilter(false) : string.Empty;
                var audioArgs = isVideo ? " -map \"[outA]\"" : string.Empty;
                var filterComplex = $"{vTrimScaleCropBuilder}{vConcatBuilder}concat=n={transitions.Length}:v=1:a=0[outV];{audioFilter}";

                leftTextPrimary.Report(string.Empty);
                rightTextPrimary.Report("Generating video...");
                RecordSingleRunProgress(0);

                try
                {
                    EnableHardwareDecoding(isVideo);
                    await StartFfmpegTranscodingProcessDefaultQuality([inputPath], GetOutputName(inputPath), isVideo ? string.Empty : "-loop 1",
                        $"-/filter_complex pipe:0 -map \"[outV]\"{audioArgs} -c:a aac",
                        (_, _, _, currentFrame) => RecordSingleRunProgress(currentFrame),
                        intermediateHandler: async process =>
                        {
                            await process.StandardInput.WriteAsync(filterComplex);
                            await process.StandardInput.FlushAsync();
                        });
                    if (HasBeenKilled())
                    {
                        ProcessCanceled();
                    }
                }
                catch (Exception e)
                {
                    var errorMessage = $"An error occurred during video creation: {e}";
                    error(errorMessage);
                }
            }
        }

        //public bool MediaIsVideo(string mediaPath)
        //{
        //    if (!File.Exists(mediaPath)) return false;
        //    var extension = Path.GetExtension(mediaPath).ToLowerInvariant();
        //    return extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".mpeg" or ".mpg";
        //}

        bool ValidateKeyFrame(KeyFrame keyFrame)
        {
            return keyFrame is { X: >= 0, Y: >= 0, Width: > 1, Height: > 1 } && keyFrame.X + keyFrame.Width <= totalWidth && keyFrame.Y + keyFrame.Height <= totalHeight;
        }

        string GetAudioComplexFilter(bool secondInput)
        {
            var ranges = new List<(TimeSpan start, TimeSpan end)>();
            foreach (var transition in transitions)
            {
                if(ranges.Count > 0 && transition.Start == ranges[^1].end)
                {
                    ranges[^1] = (ranges[^1].start, transition.End);
                }
                else ranges.Add((transition.Start, transition.End));
            }

            var atrimBuilder = new StringBuilder();
            var concatBuilder = new StringBuilder();
            for (var i = 0; i < ranges.Count; i++)
            {
                atrimBuilder.Append(
                    $"[{(secondInput ? "1" : "0")}:a]atrim=start={TimeSpanString(ranges[i].start)}:end={TimeSpanString(ranges[i].end)},asetpts=PTS-STARTPTS[a{i}];");
                concatBuilder.Append($"[a{i}]");
            }

            return $"{atrimBuilder}{concatBuilder}concat=n={ranges.Count}:v=0:a=1[outA]";
        }

        static string TimeSpanString(TimeSpan timeSpan) => timeSpan.ToString(@"hh\:mm\:ss\.fff").Replace(":", @"\\:");

        string GetOutputFolder(string path)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new NullReferenceException("The specified path is null");
            var outputFolder = Path.Combine(parentFolder, $"{inputName}_Frames");

            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
            Directory.CreateDirectory(outputFolder);
            return outputFolder;
        }

        private string GetOutputName(string path)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            outputFile = Path.Combine(parentFolder, $"{inputName}_TOURED.mp4");
            File.Delete(outputFile);
            return outputFile;
        }

        async Task Setup(bool useSingleRunMethod)
        {
            if(!useSingleRunMethod) folder = GetOutputFolder(inputPath);

            await StartFfmpegProcess($"-i \"{inputPath}\"", (sender, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                //Debug.WriteLine(args.Data);
                if (totalWidth + totalHeight == 0)
                {
                    MatchCollection matchCollection = Regex.Matches(args.Data, @"\s*Stream #0:0.+?: .+, (\d+)x(\d+).+");
                    if (matchCollection.Count == 0) return;
                    totalWidth = int.Parse(matchCollection[0].Groups[1].Value);
                    totalHeight = int.Parse(matchCollection[0].Groups[2].Value);
                }
            });

            for (var i = 0; i < transitions.Length; i++)
            {
                var transition = transitions[i];
                var frameCount = GetTransitionFrameCount(transition);
                frameCount += i == 0 || !Equals(transition.StartKeyFrame, transitions[i - 1].EndKeyFrame) ? 1 : 0; //Add one for the first frame of each transition if it's not the same as the last frame of the previous transition
                frameCountsPerTransition[i] = frameCount;
                totalFrames += frameCount;
            }

            currentTransition = 1;
        }

        void CleanUp()
        {
            if (!dontDeleteFrames) Directory.Delete(folder, true);
        }

        int GetTransitionFrameCount(Transition transition) => Convert.ToInt32(fps * transition.Duration.TotalSeconds) - 1;

        async Task<int> ProcessTransitionFrames(Transition transition, int totalFramesSoFar)
        {
            var totalFramesExcludingFirst = (double)GetTransitionFrameCount(transition);

            var xChunk = (transition.EndKeyFrame.X - transition.StartKeyFrame.X) / totalFramesExcludingFirst;
            var yChunk = (transition.EndKeyFrame.Y - transition.StartKeyFrame.Y) / totalFramesExcludingFirst;
            var widthChunk = (transition.EndKeyFrame.Width - transition.StartKeyFrame.Width) / totalFramesExcludingFirst;
            var heightChunk = (transition.EndKeyFrame.Height - transition.StartKeyFrame.Height) / totalFramesExcludingFirst;
            var frameTimeChunk = isVideo ? transition.Duration / totalFramesExcludingFirst : TimeSpan.Zero;
            double xDiff = 0, yDiff = 0, widthDiff = 0, heightDiff = 0;
            var frameTimeDiff = TimeSpan.Zero;
            var lastShift = new KeyFrame();
            var currentShift = new KeyFrame();

            for (var i = 1; i <= totalFramesExcludingFirst; i++)
            {
                xDiff += xChunk;
                yDiff += yChunk;
                widthDiff += widthChunk;
                heightDiff += heightChunk;
                frameTimeDiff += frameTimeChunk;
                currentShift.X = transition.StartKeyFrame.X + xDiff;
                currentShift.Y = transition.StartKeyFrame.Y + yDiff;
                currentShift.Width = transition.StartKeyFrame.Width + widthDiff;
                currentShift.Height = transition.StartKeyFrame.Height + heightDiff;
                if (isVideo)
                {
                    await GenerateFrame(currentShift, totalFramesSoFar + i, transition.Start + frameTimeDiff);
                }
                else
                {
                    if (Equals(currentShift, lastShift)) CopyFrame(totalFramesSoFar + i - 1, 1);
                    else await GenerateFrame(currentShift, totalFramesSoFar + i);
                }
                RecordFrameGenerationProgress(totalFramesSoFar + i, i + 1);
                lastShift = currentShift;
                await CheckPause();
                if (isCanceled) return -1;
            }

            return (int)totalFramesExcludingFirst;
        }

        async Task GenerateFrame(KeyFrame keyFrame, int frameNumber, TimeSpan frameTimePoint = default)
        {
            var seek = frameTimePoint == TimeSpan.Zero ? string.Empty : $"-ss {frameTimePoint} ";
            await StartFfmpegProcess($"-i \"{inputPath}\" {seek}-frames:v 1 -vf \"crop={keyFrame.Width}:{keyFrame.Height}:{keyFrame.X}:{keyFrame.Y}:exact=1,scale=w={width}:h={height}:flags=lanczos+accurate_rnd+full_chroma_int+full_chroma_inp,format=rgb24,setsar=1\" \"{folder}/frame{frameNumber:D8}.png\"");
        }

        void CopyFrame(int frameNumber, int amountOfCopies)
        {
            var framePath = $"{folder}/frame{frameNumber:D8}.png";
            for (var j = 1; j <= amountOfCopies; j++)
            {
                File.Copy(framePath, $"{folder}/frame{frameNumber + j:D8}.png");
            }
        }

        public override Task Cancel()
        {
            isCanceled = true;
            return base.Cancel();
        }

        async Task CheckPause()
        {
            if (isPaused)
            {
                try
                {
                    await Task.Delay(-1, pauseTokenSource.Token);
                }
                catch (TaskCanceledException) { }
            }
        }

        bool CheckCanceled()
        {
            if (isCanceled)
            {
                isCanceled = false;
                ProcessCanceled();
                return true;
            }
            return false;
        }

        void ProcessCanceled()
        {
            CleanUp();
            var errorMessage = "Operation was canceled";
            error(errorMessage);
        }

        void RecordFrameGenerationProgress(int currentFrame, int currentTransitionFrame)
        {
            var totalTransitionFrames = frameCountsPerTransition[currentTransition - 1];
            progressPrimary.Report((double)currentFrame / totalFrames * ProgressMax);
            centerTextPrimary.Report($"{currentFrame} / {totalFrames}");
            progressSecondary.Report((double)currentTransitionFrame / totalTransitionFrames * ProgressMax);
            centerTextSecondary.Report($"{currentTransitionFrame} / {totalTransitionFrames}");
        }

        private void RecordMergeProgress(int currentFrame)
        {
            progressSecondary.Report((double)currentFrame / totalFrames * ProgressMax);
            centerTextSecondary.Report($"{currentFrame} / {totalFrames}");
        }

        private void RecordSingleRunProgress(int currentFrame)
        {
            progressPrimary.Report((double)currentFrame / totalFrames * ProgressMax);
            centerTextPrimary.Report($"{currentFrame} / {totalFrames}");
        }

        public struct KeyFrame
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }

            public override string ToString()
            {
                return $"({X}, {Y}, {Width}, {Height})";
            }
        }

        public struct Transition
        {
            public KeyFrame StartKeyFrame { get; set; }
            public KeyFrame EndKeyFrame { get; set; }
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public TimeSpan Duration => End - Start;
        }
    }
}
