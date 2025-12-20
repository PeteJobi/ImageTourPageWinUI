using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        public async Task<Payload> Animate(string inputFileName, bool isVideo, int outputWidth, int outputHeight, double outputFps, IEnumerable<Transition> transitionSteps, bool dontDeleteGeneratedFrames = false)
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

            if (!transitions.Any())
            {
                return new Payload
                {
                    ErrorMessage = "No transitions specified"
                };
            }
            if (width * height == 0)
            {
                return new Payload
                {
                    ErrorMessage = "Both output dimensions should be greater than 0"
                };
            }
            if (width % 2 != 0 || height % 2 != 0)
            {
                return new Payload
                {
                    ErrorMessage = "Both output dimensions should be divisible by 2"
                };
            }
            if (!File.Exists(inputPath))
            {
                return new Payload
                {
                    ErrorMessage = "The input file could not be found"
                };
            }

            await Setup();

            foreach (var transition in transitions)
            {
                if (!ValidateKeyFrame(transition.StartKeyFrame))
                {
                    return new Payload
                    {
                        ErrorMessage = $"{transition.StartKeyFrame} exceeds the bounds of the input image"
                    };
                }
                if (!ValidateKeyFrame(transition.EndKeyFrame))
                {
                    return new Payload
                    {
                        ErrorMessage = $"{transition.EndKeyFrame} exceeds the bounds of the input image"
                    };
                }
            }

            var lastKeyFrame = new KeyFrame();
            var lastFrame = 0;

            try
            {
                var timeCovered = TimeSpan.Zero;
                isGeneratingFrames = true;
                rightTextPrimary.Report("Generating frames...");
                foreach (var transition in transitions)
                {
                    rightTextPrimary.Report($"{currentTransition} / {transitions.Length}");
                    var startNum = currentTransition * 2 - 1;
                    leftTextPrimary.Report($"{startNum} -> {startNum + 1}");

                    if (!Equals(transition.StartKeyFrame, lastKeyFrame))
                    {
                        lastFrame++;
                        await GenerateFrame(transition.StartKeyFrame, lastFrame, isVideo ? timeCovered : TimeSpan.Zero); //Generate first frame
                        RecordFrameGenerationProgress(lastFrame, 1);
                        await CheckPause();
                        var cancelPayload = CheckCanceled();
                        if (cancelPayload != null) return (Payload)cancelPayload;
                    }

                    var transitionFrameCount = await ProcessTransitionFrames(transition, lastFrame, timeCovered);
                    if (transitionFrameCount == -1) return (Payload)CheckCanceled();
                    lastFrame += transitionFrameCount;
                    lastKeyFrame = transition.EndKeyFrame;
                    timeCovered += transition.Duration;
                    currentTransition++;
                }
                isGeneratingFrames = false;
            }
            catch (Exception e)
            {
                CleanUp();
                return new Payload
                {
                    ErrorMessage = $"An error occurred during frame generation: {e}"
                };
            }

            try
            {
                leftTextPrimary.Report(string.Empty);
                rightTextPrimary.Report("Merging frames...");
                var outputPath = GetOutputName(inputPath);
                await StartFfmpegProcess($"-r {fps} -i \"{folder}/frame%08d.png\" -c:v libx265 -crf 18 -vf scale=out_color_matrix=bt709,format=yuv420p \"{outputPath}\"", (_, _, _, currentFrame) =>
                {
                    RecordMergeProgress(currentFrame);
                });
                if (HasBeenKilled()) return ProcessCanceled();
            }
            catch (Exception e)
            {
                CleanUp();
                return new Payload
                {
                    ErrorMessage = $"An error occurred during video creation: {e}"
                };
            }
            CleanUp();

            return new Payload
            {
                Success = true,
                FramesGenerated = lastFrame
            };
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

        async Task Setup()
        {
            folder = GetOutputFolder(inputPath);

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

        async Task<int> ProcessTransitionFrames(Transition transition, int totalFramesSoFar, TimeSpan totalTimeSoFar)
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
                    await GenerateFrame(currentShift, totalFramesSoFar + i, totalTimeSoFar + frameTimeDiff);
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
            await StartFfmpegProcess($"{seek}-i \"{inputPath}\" -frames:v 1 -vf \"crop={keyFrame.Width}:{keyFrame.Height}:{keyFrame.X}:{keyFrame.Y}:exact=1,scale=w={width}:h={height}:flags=lanczos+accurate_rnd+full_chroma_int+full_chroma_inp,format=rgb24,setsar=1\" \"{folder}/frame{frameNumber:D8}.png\"");
        }

        void CopyFrame(int frameNumber, int amountOfCopies)
        {
            var framePath = $"{folder}/frame{frameNumber:D8}.png";
            for (var j = 1; j <= amountOfCopies; j++)
            {
                File.Copy(framePath, $"{folder}/frame{frameNumber + j:D8}.png");
            }
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

        Payload? CheckCanceled()
        {
            if (isCanceled)
            {
                isCanceled = false;
                return ProcessCanceled();
            }
            return null;
        }

        Payload ProcessCanceled()
        {
            CleanUp();
            return new Payload
            {
                ErrorMessage = "Operation was canceled"
            };
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
            public TimeSpan Duration { get; set; }
        }

        public struct Payload
        {
            public bool Success { get; set; }
            public int FramesGenerated { get; set; }
            public string ErrorMessage { get; set; }
        }
    }
}
