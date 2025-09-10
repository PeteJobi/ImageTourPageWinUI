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

namespace ImageTourPage
{
    public class ImageTourProcessor
    {
        readonly string ffmpegPath;
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
        private IProgress<ValueProgress>? progress;
        private int[] frameCountsPerTransition;
        private bool dontDeleteFrames;
        private bool hasBeenKilled;
        private bool isVideo;
        private bool isGeneratingFrames;
        private bool isPaused;
        private bool isCanceled;
        private CancellationTokenSource pauseTokenSource = new();
        private Process? currentProcess;

        public ImageTourProcessor(string ffmpegPath)
        {
            this.ffmpegPath = ffmpegPath;
        }

        public async Task<Payload> Animate(string inputFileName, bool isVideo, int outputWidth, int outputHeight, double outputFps, IEnumerable<Transition> transitionSteps, Action<string> setFile, IProgress<ValueProgress>? progress = default, bool dontDeleteGeneratedFrames = false)
        {
            inputPath = inputFileName;
            width = outputWidth;
            height = outputHeight;
            fps = outputFps;
            this.isVideo = isVideo;
            transitions = transitionSteps.ToArray();
            frameCountsPerTransition = new int[transitions.Length];
            this.progress = progress;
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
            if (!File.Exists(ffmpegPath))
            {
                return new Payload
                {
                    ErrorMessage = "FFMPEG is required to run this program. Put the ffmpeg executable in the same folder that contains this program"
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
                foreach (var transition in transitions)
                {
                    if (!Equals(transition.StartKeyFrame, lastKeyFrame))
                    {
                        lastFrame++;
                        await GenerateFrame(transition.StartKeyFrame, lastFrame, timeCovered); //Generate first frame
                        RecordProgress(lastFrame, 1);
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
                var outputPath = GetOutputName(inputPath, setFile);
                var x = $"-r {fps} -i \"{folder}/frame%08d.png\" -c:v libx265 -vf scale=out_color_matrix=bt709,format=yuv420p \"{outputPath}\"";
                await StartProcess(ffmpegPath, x, null, (sender, args) =>
                {
                    if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) return;
                    Debug.WriteLine(args.Data);
                    var matchCollection = Regex.Matches(args.Data, @"^frame=\s*(\d+).+");
                    if (matchCollection.Count == 0) return;
                    RecordProgress(int.Parse(matchCollection[0].Groups[1].Value), -1);
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
            string inputName = Path.GetFileNameWithoutExtension(path);
            string parentFolder = Path.GetDirectoryName(path) ?? throw new NullReferenceException("The specified path is null");
            string outputFolder = Path.Combine(parentFolder, $"{inputName}_Frames");

            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
            Directory.CreateDirectory(outputFolder);
            return outputFolder;
        }

        private static string GetOutputName(string path, Action<string> setFile)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            var outputFile = Path.Combine(parentFolder, $"{inputName}_TOURED.mp4");
            setFile(outputFile);
            File.Delete(outputFile);
            return outputFile;
        }

        async Task Setup()
        {
            folder = GetOutputFolder(inputPath);

            await StartProcess(ffmpegPath, $"-i \"{inputPath}\"", null, (sender, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                //Console.WriteLine(args.Data);
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
                RecordProgress(totalFramesSoFar + i, i + 1);
                lastShift = currentShift;
                await CheckPause();
                if (isCanceled) return -1;
            }

            return (int)totalFramesExcludingFirst;
        }

        async Task GenerateFrame(KeyFrame keyFrame, int frameNumber, TimeSpan frameTimePoint = default)
        {
            var seek = frameTimePoint == TimeSpan.Zero ? string.Empty : $"-ss {frameTimePoint} ";
            await StartProcess(ffmpegPath, $"{seek}-i \"{inputPath}\" -frames:v 1 -vf \"crop={keyFrame.Width}:{keyFrame.Height}:{keyFrame.X}:{keyFrame.Y}:exact=1,scale=w={width}:h={height}:flags=lanczos+accurate_rnd+full_chroma_int+full_chroma_inp\" \"{folder}/frame{frameNumber:D8}.png\"", null, (sender, args) =>
            {
                //if (string.IsNullOrWhiteSpace(args.Data) || hasBeenKilled) Console.WriteLine("N");
                if(args.Data?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true) Debug.WriteLine(args.Data);
                //Debug.WriteLine(args.Data);
            });
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

        void RecordProgress(int currentFrame, int currentTransitionFrame) => progress?.Report(new ValueProgress
        {
            CurrentFrame = currentFrame, 
            TotalFrames = totalFrames, 
            CurrentTransition = currentTransition,
            CurrentTransitionFrame = currentTransitionFrame,
            TotalTransitionFrames = currentTransitionFrame == -1 ? -1 : frameCountsPerTransition[currentTransition - 1]
        });

        public void ViewFile(string file)
        {
            var info = new ProcessStartInfo();
            info.FileName = "explorer";
            info.Arguments = $"/e, /select, \"{file}\"";
            Process.Start(info);
        }

        //public async Task CreateVideoFromImage(string imagePath)
        //{

        //}

        bool HasBeenKilled()
        {
            if (!hasBeenKilled) return false;
            hasBeenKilled = false;
            return true;
        }

        private static void SuspendProcess(Process process)
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                SuspendThread(pOpenThread);
                CloseHandle(pOpenThread);
            }
        }

        public async Task Cancel(string outputFile)
        {
            if (isGeneratingFrames)
            {
                isCanceled = true;
                if (isPaused) Resume();
                return;
            }
            if (currentProcess == null) return;
            currentProcess.Kill();
            await currentProcess.WaitForExitAsync();
            hasBeenKilled = true;
            currentProcess = null;
            if (Directory.Exists(outputFile)) Directory.Delete(outputFile, true);
        }

        public void Pause()
        {
            if (isGeneratingFrames)
            {
                isPaused = true;
                return;
            }
            if (currentProcess == null) return;
            SuspendProcess(currentProcess);
        }

        public void Resume()
        {
            if (isGeneratingFrames && isPaused)
            {
                isPaused = false;
                pauseTokenSource.Cancel();
                pauseTokenSource = new CancellationTokenSource();
                return;
            }
            if (currentProcess == null) return;
            if (currentProcess.ProcessName == string.Empty)
                return;

            foreach (ProcessThread pT in currentProcess.Threads)
            {
                var pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                int suspendCount;
                do
                {
                    suspendCount = ResumeThread(pOpenThread);
                } while (suspendCount > 0);

                CloseHandle(pOpenThread);
            }
        }

        async Task StartProcess(string processFileName, string arguments, DataReceivedEventHandler? outputEventHandler, DataReceivedEventHandler? errorEventHandler)
        {
            Process ffmpeg = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = processFileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
                EnableRaisingEvents = true
            };
            ffmpeg.OutputDataReceived += outputEventHandler;
            ffmpeg.ErrorDataReceived += errorEventHandler;
            ffmpeg.Start();
            ffmpeg.BeginErrorReadLine();
            ffmpeg.BeginOutputReadLine();
            currentProcess = ffmpeg;
            await ffmpeg.WaitForExitAsync();
            ffmpeg.Dispose();
            currentProcess = null;
        }

        [Flags]
        public enum ThreadAccess : int
        {
            SUSPEND_RESUME = (0x0002)
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll")]
        static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")]
        static extern int ResumeThread(IntPtr hThread);
        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

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

        public struct ValueProgress
        {
            public int CurrentFrame { get; set; }
            public int TotalFrames { get; set; }
            public int CurrentTransition { get; set; }
            public int CurrentTransitionFrame { get; set; }
            public int TotalTransitionFrames { get; set; }
        }
    }
}
