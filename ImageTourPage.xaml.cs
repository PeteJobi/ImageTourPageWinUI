using DraggerResizer;
using ImageTourPage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Orientation = DraggerResizer.Orientation;
using Path = System.IO.Path;
using Transition = ImageTourPage.Transition;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ImageTour
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ImageTourPage : Page
    {
        private readonly DraggerResizer.DraggerResizer resizer;
        private Point _lastPoint, _lastCanvasPressedPoint;
        private bool _isPanning;
        private bool userIsHandlingFrames;
        private DispatcherTimer _dashTimer;
        private double _offset;
        private const double animLineDashArray1 = 6;
        private const double animLineDashArray2 = 3;
        private const int defaultDurationInSeconds = 5;
        private const double progressMax = 1_000_000;
        private bool videoProgressChangedByCode;
        private Dictionary<FrameworkElement, KeyFrameProps> frameProps = [];
        private Dictionary<KeyFrame, FrameworkElement> keyFrameToElement = [];
        private List<AnimLines> allAnimLines = [];
        private string? navigateTo;
        private string? outputFile;
        private string mediaPath;
        private bool isVideo;
        private readonly DataTemplate keyFrameTemplate = (DataTemplate)Application.Current.Resources["KeyFrameTemplate"];
        private readonly DataTemplate multiKeyFrameLabelTemplate = (DataTemplate)Application.Current.Resources["MultiKeyFrameLabelTemplate"];
        private ObservableCollection<Transition> transitions = [];
        private TourMainModel viewModel;
        private ImageTourProcessor tourProcessor;

        public ImageTourPage()
        {
            InitializeComponent();
            resizer = new DraggerResizer.DraggerResizer();
            viewModel = new TourMainModel();
            OverallTourProgress.Maximum = CurrentTourProgress.Maximum = progressMax;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var props = (TourProps)e.Parameter;
            tourProcessor = new ImageTourProcessor(props.FfmpegPath);
            isVideo = props.MediaPath.EndsWith(".mp4") || props.MediaPath.EndsWith(".mkv");
            mediaPath = props.MediaPath;
            navigateTo = props.TypeToNavigateTo;
            MediaName.Text = Path.GetFileName(mediaPath);
            if(isVideo)
            {
                Video.Source = MediaSource.CreateFromUri(new Uri(mediaPath));
                Video.MediaPlayer.PlaybackSession.NaturalDurationChanged += PlaybackSessionOnNaturalDurationChanged;
                Video.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            }
            else Image.Source = new BitmapImage(new Uri(mediaPath));
            base.OnNavigatedTo(e);
        }

        private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ContentCanvas);
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta > 0 ? 1.1 : (1 / 1.1); //Increase or decrease zoom by 10%

            var prevScaleX = ZoomTransform.ScaleX;
            var newScaleX = ZoomTransform.ScaleX * delta;
            var prevScaleY = ZoomTransform.ScaleY;
            var newScaleY = ZoomTransform.ScaleY * delta;

            ZoomTransform.ScaleX = newScaleX;
            ZoomTransform.ScaleY = newScaleY;

            // Adjust translate to zoom around mouse
            PanTransform.X -= point.Position.X * (newScaleX - prevScaleX);
            PanTransform.Y -= point.Position.Y * (newScaleY - prevScaleY);
        }

        private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isPanning = true;
            _lastPoint = e.GetCurrentPoint(CanvasContainer).Position;
            _lastCanvasPressedPoint = e.GetCurrentPoint(ContentCanvas).Position;
            ContentCanvas.CapturePointer(e.Pointer);
        }

        private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isPanning = false;
            ContentCanvas.ReleasePointerCaptures();
        }

        private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                var currentPoint = e.GetCurrentPoint(CanvasContainer).Position;
                PanTransform.X += currentPoint.X - _lastPoint.X;
                PanTransform.Y += currentPoint.Y - _lastPoint.Y;
                _lastPoint = currentPoint;
            }
        }

        private void PlaybackSessionOnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                VideoProgressSlider.Maximum = sender.NaturalDuration.TotalSeconds;
                VideoProgressSlider.Value = 0;
                SetVideoTime();
            });
        }

        private async void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Playing) await AnimateSeeker(sender);
            else DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => viewModel.IsPlaying = false);
        }

        private async Task AnimateSeeker(MediaPlaybackSession session)
        {
            const int frameTime24Fps = 1000 / 24;
            while (session.PlaybackState == MediaPlaybackState.Playing)
            {
                if (DispatcherQueue == null) return;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    videoProgressChangedByCode = true;
                    VideoProgressSlider.Value = session.Position.TotalSeconds;
                    videoProgressChangedByCode = false;
                    SetVideoTime();
                });
                await Task.Delay(frameTime24Fps);
            }
        }

        private void SetVideoTime() => VideoTime.Text = $@"{Video.MediaPlayer.PlaybackSession.Position:hh\:mm\:ss\.fff} / {Video.MediaPlayer.PlaybackSession.NaturalDuration:hh\:mm\:ss\.fff}";

        private void VideoProgressSlider_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (videoProgressChangedByCode) return;
            Video.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            SetVideoTime();
        }

        private void PlayPause(object sender, RoutedEventArgs e)
        {
            if (Video.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                Video.MediaPlayer.Pause();
                viewModel.IsPlaying = false;
            }
            else
            {
                Video.MediaPlayer.Play();
                viewModel.IsPlaying = true;
            }
        }

        private HandlingCallbacks GetNewHandlingCallback(FrameworkElement owner)
        {
            return new HandlingCallbacks
            {
                DragStarted = () => userIsHandlingFrames = true,
                ResizeStarted = _ => userIsHandlingFrames = true,
                BeforeDragging = point => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                BeforeResizing =
                    (point, _) => new Point(point.X / ZoomTransform.ScaleX, point.Y / ZoomTransform.ScaleY),
                AfterDragging = _ => UpdateAnimLinesAndCoords(owner),
                AfterResizing = (_, _) => UpdateAnimLinesAndCoords(owner),
                DragCompleted = () => { userIsHandlingFrames = false; CheckClumps(owner); },
                ResizeCompleted = _ => { userIsHandlingFrames = false; CheckAspectRatio(owner); }
            };
        }

        private HandlingParameters GetAspectRatioParam(bool lockedAspectRatio) => new() { KeepAspectRatio = lockedAspectRatio };

        private void CheckClumps(FrameworkElement keyframeElement, bool aboutToBeDeleted = false)
        {
            var prop = frameProps[keyframeElement];
            var keyFrame = prop.KeyFrame;
            if (prop.KeyFrameLabel.PartOfClump)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    var clumpLabels = (ObservableCollection<KeyFrameLabel>)prop.ClumpGridView!.ItemsSource;
                    clumpLabels.Remove(prop.KeyFrameLabel);
                    prop.KeyFrameLabel.PartOfClump = false;
                    prop.ClumpGridView = null;
                    if (clumpLabels.Count == 1)
                    {
                        var lastLabelProp = frameProps.Values.FirstOrDefault(p => p.KeyFrameLabel == clumpLabels[0]);
                        if (lastLabelProp == null) return;
                        lastLabelProp.KeyFrameLabel.PartOfClump = false;
                        ContentCanvas.Children.Remove(lastLabelProp.ClumpGridView);
                        lastLabelProp.ClumpGridView = null;
                    }
                });
            }
            if (aboutToBeDeleted) return;
            foreach (var transition in transitions)
            {
                if (keyFrame != transition.StartKeyFrame && prop.KeyFrame2 != transition.StartKeyFrame &&
                    Math.Abs(transition.StartKeyFrame.X - keyFrame.X) < 20 && Math.Abs(transition.StartKeyFrame.Y - keyFrame.Y) < 20)
                {
                    AddToClump(transition.StartKeyFrame);
                    break;
                }
                if (keyFrame != transition.EndKeyFrame && prop.KeyFrame2 != transition.EndKeyFrame &&
                    Math.Abs(transition.EndKeyFrame.X - keyFrame.X) < 20 && Math.Abs(transition.EndKeyFrame.Y - keyFrame.Y) < 20)
                {
                    AddToClump(transition.EndKeyFrame);
                    break;
                }
            }

            void AddToClump(KeyFrame matchingKeyFrame)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    prop.KeyFrameLabel.PartOfClump = true;
                    var mkfProp = frameProps[keyFrameToElement[matchingKeyFrame]];
                    if (mkfProp.KeyFrameLabel.PartOfClump)
                    {
                        var clumpLabels = (ObservableCollection<KeyFrameLabel>)mkfProp.ClumpGridView!.ItemsSource;
                        clumpLabels.Add(prop.KeyFrameLabel);
                        prop.ClumpGridView = mkfProp.ClumpGridView;
                        return;
                    }
                    var clumpGridView = AddMultiKeyFrameLabel(keyFrame.X, keyFrame.Y, mkfProp.KeyFrameLabel, prop.KeyFrameLabel);
                    prop.ClumpGridView = clumpGridView;
                    mkfProp.ClumpGridView = clumpGridView;
                    mkfProp.KeyFrameLabel.PartOfClump = true;
                });
            }
        }

        private void CheckAspectRatio(FrameworkElement keyframeElement)
        {
            var prop = frameProps[keyframeElement];
            var expectedAspectRatio = OutputWidth.Value / OutputHeight.Value;
            var actualAspectRatio = (double)prop.KeyFrame.Width / prop.KeyFrame.Height;
            prop.KeyFrame.IncorrectAspectRatio = Math.Abs(expectedAspectRatio - actualAspectRatio) > 0.00001;
            if (prop.KeyFrameLabel.HoldsTwo) prop.KeyFrame2.IncorrectAspectRatio = prop.KeyFrame.IncorrectAspectRatio;
        }

        private void UpdateAnimLinesAndCoords(FrameworkElement keyframeElement)
        {
            var top = resizer.GetElementTop(keyframeElement);
            var left = resizer.GetElementLeft(keyframeElement);
            var (fromLines, toLines) = frameProps[keyframeElement].AnimLines;
            if (fromLines != null) UpdateLineFrom(keyframeElement, left, top, fromLines);
            if (toLines != null) UpdateLineTo(keyframeElement, left, top, toLines);
            var keyFrame = frameProps[keyframeElement].KeyFrame;
            keyFrame.X = (int)left;
            keyFrame.Y = (int)top;
            keyFrame.Width = (int)keyframeElement.Width;
            keyFrame.Height = (int)keyframeElement.Height;
            var keyFrame2 = frameProps[keyframeElement].KeyFrame2;
            if (keyFrame2 != null)
            {
                keyFrame2.X = keyFrame.X;
                keyFrame2.Y = keyFrame.Y;
                keyFrame2.Width = keyFrame.Width;
                keyFrame2.Height = keyFrame.Height;
            }
        }

        private Line GetNewAnimLine()
        {
            var line = new Line
            {
                Stroke = (SolidColorBrush)Application.Current.Resources["AnimLineColour"],
                StrokeThickness = 2,
                StrokeDashArray = [animLineDashArray1, animLineDashArray2],
                Opacity = 0.2
            };
            ContentCanvas.Children.Add(line);
            return line;
        }

        private void LinkKeyFrameElements(FrameworkElement from, KeyFrameProps fromProps, FrameworkElement to, KeyFrameProps toProps)
        {
            var newAnimLines = new AnimLines
            {
                TopLeftLine = GetNewAnimLine(),
                TopRightLine = GetNewAnimLine(),
                BottomLeftLine = GetNewAnimLine(),
                BottomRightLine = GetNewAnimLine()
            };
            fromProps.AnimLines = (newAnimLines, fromProps.AnimLines.To);
            toProps.AnimLines = (toProps.AnimLines.From, newAnimLines);
            allAnimLines.Add(newAnimLines);
            var fromTop = resizer.GetElementTop(from);
            var fromLeft = resizer.GetElementLeft(from);
            var toTop = resizer.GetElementTop(to);
            var toLeft = resizer.GetElementLeft(to);
            UpdateLineFrom(from, fromLeft, fromTop, newAnimLines);
            UpdateLineTo(to, toLeft, toTop, newAnimLines);
        }

        private void UpdateLineFrom(FrameworkElement frame, double left, double top, AnimLines animLines)
        {
            animLines.TopLeftLine.X1 = left;
            animLines.TopLeftLine.Y1 = top;
            animLines.TopRightLine.X1 = left + frame.Width;
            animLines.TopRightLine.Y1 = top;
            animLines.BottomLeftLine.X1 = left;
            animLines.BottomLeftLine.Y1 = top + frame.Height;
            animLines.BottomRightLine.X1 = left + frame.Width;
            animLines.BottomRightLine.Y1 = top + frame.Height;
        }

        private void UpdateLineTo(FrameworkElement frame, double left, double top, AnimLines animLines)
        {
            animLines.TopLeftLine.X2 = left;
            animLines.TopLeftLine.Y2 = top;
            animLines.TopRightLine.X2 = left + frame.Width;
            animLines.TopRightLine.Y2 = top;
            animLines.BottomLeftLine.X2 = left;
            animLines.BottomLeftLine.Y2 = top + frame.Height;
            animLines.BottomRightLine.X2 = left + frame.Width;
            animLines.BottomRightLine.Y2 = top + frame.Height;
        }

        private void Video_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SizeRetrieved(e.NewSize.Width, e.NewSize.Height);
        }

        private void Image_OnImageOpened(object sender, RoutedEventArgs e)
        {
            SizeRetrieved(Image.ActualWidth, Image.ActualHeight);
        }

        private void SizeRetrieved(double width, double height)
        {
            ContentCanvas.Width = width;
            ContentCanvas.Height = height;
            const int defaultSize = 500;
            var canvasWiderThanView = FitCanvasToView();
            int initialFrameSize = (int)Math.Min(defaultSize, (canvasWiderThanView ? height : width) - 20);
            OutputWidth.Value = OutputHeight.Value = initialFrameSize;

            InitLinesAnimation();
            var initialFramePosX = (int)((width - initialFrameSize) / 2);
            var initialFramePosY = (int)(height - initialFrameSize) / 2;
            AddTransitionKeyFrames(initialFramePosX, initialFramePosY, initialFrameSize, initialFrameSize);
        }

        private bool FitCanvasToView()
        {
            bool canvasWiderThanView;
            //GeneralTransform gt = ContentCanvas.TransformToVisual(CanvasContainer);
            //Point offset = gt.TransformPoint(new Point(0, 0));
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < ContentCanvas.Width / ContentCanvas.Height)
            {
                ZoomTransform.ScaleX = ZoomTransform.ScaleY = CanvasContainer.ActualWidth / ContentCanvas.Width; // Fit to width
                var scaledHeight = CanvasContainer.ActualWidth * ContentCanvas.Height / ContentCanvas.Width;
                PanTransform.Y = (CanvasContainer.ActualHeight - scaledHeight) / 2; // Center vertically
                PanTransform.X = 0;
                canvasWiderThanView = true;
            }
            else
            {
                ZoomTransform.ScaleX = ZoomTransform.ScaleY = CanvasContainer.ActualHeight / ContentCanvas.Height; // Fit to height
                var scaledWidth = CanvasContainer.ActualHeight * ContentCanvas.Width / ContentCanvas.Height;
                PanTransform.X = (CanvasContainer.ActualWidth - scaledWidth) / 2; // Center horizontally
                PanTransform.Y = 0;
                canvasWiderThanView = false;
            }
            return canvasWiderThanView;
        }

        private void InitLinesAnimation()
        {
            _dashTimer = new DispatcherTimer();
            const double interval = (double)1000 / 60; // 60 FPS
            _dashTimer.Interval = TimeSpan.FromMilliseconds(interval);
            const double chunk = interval / 500 * (animLineDashArray1 + animLineDashArray2);
            _dashTimer.Tick += (s, args) =>
            {
                _offset -= chunk;
                if (_offset < 0) _offset = animLineDashArray1 + animLineDashArray2;
                foreach (var lines in allAnimLines)
                {
                    lines.TopLeftLine.StrokeDashOffset = _offset;
                    lines.TopRightLine.StrokeDashOffset = _offset;
                    lines.BottomLeftLine.StrokeDashOffset = _offset;
                    lines.BottomRightLine.StrokeDashOffset = _offset;
                }
            };
            _dashTimer.Start();
        }

        private void AddTransitionKeyFrames(int x, int y, int width, int height)
        {
            var keyFrameElement1 = AddNewKeyFrameElement(x, y, width, height);
            var keyFrameElement2 = AddNewKeyFrameElement(x, y, width, height);
            var lastTransitionNumber = transitions.Count * 2;
            var transition = new Transition
            {
                StartKeyFrame = new KeyFrame(x, y, width, height, lastTransitionNumber + 1),
                EndKeyFrame = new KeyFrame(x, y, width, height, lastTransitionNumber + 2),
                Duration = TimeSpan.FromSeconds(defaultDurationInSeconds)
            };
            var keyFrameProps1 = new KeyFrameProps
            {
                KeyFrameLabel = new KeyFrameLabel(transition.StartKeyFrame.Number, false, true),
                KeyFrame = transition.StartKeyFrame
            };
            var keyFrameProps2 = new KeyFrameProps
            {
                KeyFrameLabel = new KeyFrameLabel(transition.EndKeyFrame.Number, false, true),
                KeyFrame = transition.EndKeyFrame
            };
            keyFrameElement1.DataContext = keyFrameProps1.KeyFrameLabel;
            keyFrameElement2.DataContext = keyFrameProps2.KeyFrameLabel;
            frameProps.Add(keyFrameElement1, keyFrameProps1);
            frameProps.Add(keyFrameElement2, keyFrameProps2);
            LinkKeyFrameElements(keyFrameElement1, keyFrameProps1, keyFrameElement2, keyFrameProps2);
            var clumpGridView = AddMultiKeyFrameLabel(x, y, keyFrameProps1.KeyFrameLabel, keyFrameProps2.KeyFrameLabel);
            keyFrameProps1.ClumpGridView = clumpGridView;
            keyFrameProps2.ClumpGridView = clumpGridView;
            keyFrameToElement[transition.StartKeyFrame] = keyFrameElement1;
            keyFrameToElement[transition.EndKeyFrame] = keyFrameElement2;
            transitions.Add(transition);
        }

        private FrameworkElement AddNewKeyFrameElement(int x, int y, int width, int height)
        {
            var keyFrameElement = (FrameworkElement)keyFrameTemplate.LoadContent();
            ContentCanvas.Children.Add(keyFrameElement);
            Canvas.SetLeft(keyFrameElement, x);
            Canvas.SetTop(keyFrameElement, y);
            keyFrameElement.Width = width;
            keyFrameElement.Height = height;
            var menuFlyout = (MenuFlyout)keyFrameElement.FindName("MenuFlyout");
            menuFlyout.Opening += (sender, o) => KeyFrameMenuFlyoutOnOpening(keyFrameElement, (MenuFlyout)sender);
            resizer.InitDraggerResizer(keyFrameElement, default(HashSet<Orientation>),
                GetAspectRatioParam(LockAspectRatioCheckBox.IsChecked ?? false), GetNewHandlingCallback(keyFrameElement));
            return keyFrameElement;
        }

        private void KeyFrameMenuFlyoutOnOpening(FrameworkElement keyframeElement, MenuFlyout menuFlyout)
        {
            var props = frameProps[keyframeElement];
            menuFlyout.Items.Clear();
            menuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Frame " + props.KeyFrameLabel.LabelDisplay,
                IsEnabled = false
            });
            menuFlyout.Items.Add(new MenuFlyoutSeparator());

            menuFlyout.Items.Add(new MenuFlyoutSubItem
            {
                Text = "Move to",
                Items =
                {
                    CreateFlyoutItem("Top center", () =>
                    {
                        resizer.PositionElement(keyframeElement, (ContentCanvas.Width - keyframeElement.Width) / 2, 0);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckClumps(keyframeElement);
                    }),
                    CreateFlyoutItem("Bottom center", () =>
                    {
                        resizer.PositionElement(keyframeElement, (ContentCanvas.Width - keyframeElement.Width) / 2, ContentCanvas.Height - keyframeElement.Height);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckClumps(keyframeElement);
                    }),
                    CreateFlyoutItem("Left center", () =>
                    {
                        resizer.PositionElement(keyframeElement, 0, (ContentCanvas.Height - keyframeElement.Height) / 2);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckClumps(keyframeElement);
                    }),
                    CreateFlyoutItem("Right center", () =>
                    {
                        resizer.PositionElement(keyframeElement, ContentCanvas.Width - keyframeElement.Width, (ContentCanvas.Height - keyframeElement.Height) / 2);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckClumps(keyframeElement);
                    }),
                    CreateFlyoutItem("Center", () =>
                    {
                        resizer.PositionElementAtCenter(keyframeElement);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckClumps(keyframeElement);
                    })
                }
            });

            var framesNotInSameClump = GetFramesNotInSameClump().ToList();
            if (framesNotInSameClump.Count > 0)
            {
                menuFlyout.Items.Add(CreateFlyoutSubItems("Copy frame position", PopulatePosSwapSubItems(false)));
                menuFlyout.Items.Add(CreateFlyoutSubItems("Swap position with frame", PopulatePosSwapSubItems(true)));
            }
            
            var framesNotSameSize = GetFramesNotSameSize().ToList();
            if (framesNotSameSize.Count > 0)
            {
                menuFlyout.Items.Add(CreateFlyoutSubItems("Copy frame size", PopulateSizeSwapSubItems(false)));
                menuFlyout.Items.Add(CreateFlyoutSubItems("Swap size with frame", PopulateSizeSwapSubItems(true)));
            }

            if (props.KeyFrame.Width != (int)OutputWidth.Value || props.KeyFrame.Height != (int)OutputHeight.Value)
            {
                menuFlyout.Items.Add(new MenuFlyoutSubItem
                {
                    Text = "Output size",
                    Items =
                    {
                        CreateFlyoutItem("Set this to output size", () =>
                        {
                            resizer.ResizeElement(keyframeElement, (int)OutputWidth.Value, (int)OutputHeight.Value, parameters: GetAspectRatioParam(false));
                            UpdateAnimLinesAndCoords(keyframeElement);
                            CheckAspectRatio(keyframeElement);
                        }),
                        CreateFlyoutItem("Set output size to this", () =>
                        {
                            OutputWidth.Value = props.KeyFrame.Width;
                            OutputHeight.Value = props.KeyFrame.Height;
                        })
                    }
                });
            }

            if (props.KeyFrameLabel.HoldsTwo)
            {
                menuFlyout.Items.Add(CreateFlyoutItem($"Separate frames {props.KeyFrameLabel.Number} and {props.KeyFrameLabel.Number + 1}", () =>
                {
                    props.KeyFrameLabel.HoldsTwo = false;
                    var newKeyFrameElement = AddNewKeyFrameElement(props.KeyFrame.X, props.KeyFrame.Y, props.KeyFrame.Width, props.KeyFrame.Height);
                    var newKeyFrameProps = new KeyFrameProps
                    {
                        KeyFrameLabel = new KeyFrameLabel(props.KeyFrameLabel.Number + 1),
                        KeyFrame = props.KeyFrame2 ?? throw new NullReferenceException("KeyFrame2 should not be null when separating frames.")
                    };
                    newKeyFrameProps.AnimLines = (props.AnimLines.From, null);
                    props.AnimLines = (null, props.AnimLines.To);
                    newKeyFrameElement.DataContext = newKeyFrameProps.KeyFrameLabel;
                    frameProps.Add(newKeyFrameElement, newKeyFrameProps);
                    keyFrameToElement[newKeyFrameProps.KeyFrame] = newKeyFrameElement;
                    UpdateAnimLinesAndCoords(newKeyFrameElement);
                    CheckClumps(newKeyFrameElement);
                }));
            }else if (props.KeyFrameLabel.Number != 1 && props.KeyFrameLabel.Number != transitions.Count * 2)
            {
                var isEndKeyframe = props.KeyFrameLabel.Number % 2 == 0;
                var numberToMergeWith = props.KeyFrameLabel.Number + (isEndKeyframe ? 1 : -1);
                var kvpToMergeWith = frameProps.First(kvp => kvp.Value.KeyFrameLabel.Number == numberToMergeWith);
                menuFlyout.Items.Add(CreateFlyoutItem($"Merge with frame {numberToMergeWith}", () =>
                {
                    CheckClumps(keyframeElement, true);
                    resizer.RemoveElement(keyframeElement);
                    frameProps.Remove(keyframeElement);
                    kvpToMergeWith.Value.KeyFrameLabel.HoldsTwo = true;
                    keyFrameToElement[props.KeyFrame] = kvpToMergeWith.Key;
                    if (isEndKeyframe)
                    {
                        kvpToMergeWith.Value.KeyFrameLabel.Number--;
                        kvpToMergeWith.Value.KeyFrame2 = kvpToMergeWith.Value.KeyFrame;
                        kvpToMergeWith.Value.KeyFrame = props.KeyFrame;
                        kvpToMergeWith.Value.AnimLines = (kvpToMergeWith.Value.AnimLines.From, props.AnimLines.To);
                    }
                    else
                    {
                        kvpToMergeWith.Value.KeyFrame2 = props.KeyFrame;
                        kvpToMergeWith.Value.AnimLines = (props.AnimLines.From, kvpToMergeWith.Value.AnimLines.To);
                    }
                    UpdateAnimLinesAndCoords(kvpToMergeWith.Key);
                }));
            }

            if (transitions.Count > 1)
            {
                if (props.KeyFrameLabel.HoldsTwo)
                {
                    menuFlyout.Items.Add(new MenuFlyoutSubItem
                    {
                        Text = "Delete",
                        Items =
                        {
                            CreateFlyoutItem(DeleteTransitionText(props.KeyFrame), () => DeleteTransitionAction(props.KeyFrame)),
                            CreateFlyoutItem($"Delete frame {props.KeyFrameLabel.LabelDisplay}", () =>
                            {
                                //Imagine transitions 1 to 2,3 to 4. We're trying to delete 2,3 and link 1 to 4. 4 will later become the new 2
                                var startTransition = transitions.First(t => t.EndKeyFrame == props.KeyFrame); //This transition is 1 to 2
                                var endTransition = transitions.First(t => t.StartKeyFrame == props.KeyFrame2); //This transition is 3 to 4
                                var newStartProps = frameProps[keyFrameToElement[startTransition.StartKeyFrame]]; //Props for 1
                                var newEndProps = frameProps[keyFrameToElement[endTransition.EndKeyFrame]]; //Props for 4
                                
                                newEndProps.AnimLines = (newEndProps.AnimLines.From, newStartProps.AnimLines.From); //Link lines from 1 to 4 (instead of 1 to 2,3)
                                UpdateAnimLinesAndCoords(keyFrameToElement[endTransition.EndKeyFrame]); //Update new lines for 4
                                startTransition.EndKeyFrame = newEndProps.KeyFrame; //4 is now the EndKeyframe for transition 1 to 2
                                startTransition.Duration += endTransition.Duration; //Transition 1 to 4 is now as long as 1 to 2 plus 3 to 4
                                newEndProps.KeyFrame.Number = newEndProps.KeyFrameLabel.Number = props.KeyFrame.Number; //Change 4 to 2

                                //Remove frame 2,3
                                CheckClumps(keyframeElement, true);
                                resizer.RemoveElement(keyframeElement);
                                frameProps.Remove(keyframeElement);
                                keyFrameToElement.Remove(props.KeyFrame);
                                keyFrameToElement.Remove(props.KeyFrame2);

                                //Remove lines linking 2,3 to 4
                                ContentCanvas.Children.Remove(props.AnimLines.From.TopLeftLine);
                                ContentCanvas.Children.Remove(props.AnimLines.From.TopRightLine);
                                ContentCanvas.Children.Remove(props.AnimLines.From.BottomLeftLine);
                                ContentCanvas.Children.Remove(props.AnimLines.From.BottomRightLine);

                                var endTransitionIndex = transitions.IndexOf(endTransition);
                                transitions.RemoveAt(endTransitionIndex); //Remove transition 3 to 4
                                RenumberFrames(endTransitionIndex);
                            }),
                            CreateFlyoutItem(DeleteTransitionText(props.KeyFrame2), () => DeleteTransitionAction(props.KeyFrame2)),
                        }
                    });
                }
                else
                {
                    menuFlyout.Items.Add(CreateFlyoutItem(DeleteTransitionText(props.KeyFrame), () => DeleteTransitionAction(props.KeyFrame)));
                }
            }

            MenuFlyoutItem CreateFlyoutItem(string text, Action action)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += (s, e) => action();
                return item;
            }

            MenuFlyoutSubItem CreateFlyoutSubItems(string text, List<MenuFlyoutItem> subItems)
            {
                var parent = new MenuFlyoutSubItem { Text = text };
                foreach (var subItem in subItems)
                {
                    parent.Items.Add(subItem);
                }
                return parent;
            }

            IEnumerable<FrameworkElement> GetFramesNotInSameClump()
            {
                foreach (var framePropKvp in frameProps)
                {
                    if (framePropKvp.Key == keyframeElement) continue;
                    if(props.KeyFrameLabel.PartOfClump && framePropKvp.Value.ClumpGridView == props.ClumpGridView) continue;
                    yield return framePropKvp.Key;
                }
            }

            IEnumerable<FrameworkElement> GetFramesNotSameSize()
            {
                foreach (var framePropKvp in frameProps)
                {
                    if (framePropKvp.Key == keyframeElement) continue;
                    if(props.KeyFrame.Width != framePropKvp.Value.KeyFrame.Width || props.KeyFrame.Height != framePropKvp.Value.KeyFrame.Height)
                        yield return framePropKvp.Key;
                }
            }

            List<MenuFlyoutItem> PopulatePosSwapSubItems(bool swap)
            {
                var list = new List<MenuFlyoutItem>();
                var currentX = props.KeyFrame.X;
                var currentY = props.KeyFrame.Y;
                foreach (var frame in framesNotInSameClump)
                {
                    var currFrameProps = frameProps[frame];
                    var item = CreateFlyoutItem("Frame " + currFrameProps.KeyFrameLabel.LabelDisplay, () =>
                    {
                        resizer.PositionElement(keyframeElement, currFrameProps.KeyFrame.X, currFrameProps.KeyFrame.Y);
                        UpdateAnimLinesAndCoords(keyframeElement);
                        if (swap)
                        {
                            resizer.PositionElement(frame, currentX, currentY);
                            UpdateAnimLinesAndCoords(frame);
                            CheckClumps(frame);
                        }
                        CheckClumps(keyframeElement);
                    });
                    list.Add(item);
                }
                return list;
            }

            List<MenuFlyoutItem> PopulateSizeSwapSubItems(bool swap)
            {
                var list = new List<MenuFlyoutItem>();
                var currentWidth = props.KeyFrame.Width;
                var currentHeight = props.KeyFrame.Height;
                foreach (var frame in framesNotSameSize)
                {
                    var currFrameProps = frameProps[frame];
                    var item = CreateFlyoutItem("Frame " + currFrameProps.KeyFrameLabel.LabelDisplay, () =>
                    {
                        resizer.ResizeElement(keyframeElement, currFrameProps.KeyFrame.Width, currFrameProps.KeyFrame.Height, parameters: GetAspectRatioParam(false));
                        UpdateAnimLinesAndCoords(keyframeElement);
                        CheckAspectRatio(keyframeElement);
                        if (swap)
                        {
                            resizer.ResizeElement(frame, currentWidth, currentHeight, parameters: GetAspectRatioParam(false));
                            UpdateAnimLinesAndCoords(frame);
                            CheckAspectRatio(frame);
                        }
                    });
                    list.Add(item);
                }
                return list;
            }

            string DeleteTransitionText(KeyFrame keyframeFromTransition)
            {
                var startNum = keyframeFromTransition.Number - (keyframeFromTransition.IsStartKeyframe ? 0 : 1);
                var endNum = keyframeFromTransition.Number + (keyframeFromTransition.IsStartKeyframe ? 1 : 0);
                return $"Delete frames {startNum} and {endNum}";
            }

            void DeleteTransitionAction(KeyFrame keyframeFromTransition)
            {
                var transitionIndex = 0;
                for (var i = 0; i < transitions.Count; i++)
                {
                    if (transitions[i].StartKeyFrame != keyframeFromTransition &&
                        transitions[i].EndKeyFrame != keyframeFromTransition) continue;
                    transitionIndex = i;
                    break;
                }
                var transition = transitions[transitionIndex];
                KeyFrame[] keyframes = [transition.StartKeyFrame, transition.EndKeyFrame];
                foreach (var keyframe in keyframes)
                {
                    var isStartFrame = keyframe == transition.StartKeyFrame;
                    var frameElement = keyFrameToElement[keyframe];
                    var frameProp = frameProps[frameElement];
                    if (frameProp.KeyFrameLabel.HoldsTwo)
                    {
                        frameProp.KeyFrameLabel.HoldsTwo = false;
                        if (isStartFrame)
                        {
                            frameProp.KeyFrame2 = null;
                        }
                        else
                        {
                            frameProp.KeyFrame = frameProp.KeyFrame2;
                            frameProp.KeyFrame2 = null;
                        }
                    }
                    else
                    {
                        CheckClumps(frameElement, true);
                        resizer.RemoveElement(frameElement);
                        frameProps.Remove(frameElement);
                    }
                    if (isStartFrame)
                    {
                        ContentCanvas.Children.Remove(frameProp.AnimLines.From.TopLeftLine);
                        ContentCanvas.Children.Remove(frameProp.AnimLines.From.TopRightLine);
                        ContentCanvas.Children.Remove(frameProp.AnimLines.From.BottomLeftLine);
                        ContentCanvas.Children.Remove(frameProp.AnimLines.From.BottomRightLine);
                    }
                    keyFrameToElement.Remove(keyframe);
                }
                transitions.RemoveAt(transitionIndex);
                RenumberFrames(transitionIndex);
            }

            void RenumberFrames(int deletedTransitionIndex)
            {
                for (var i = deletedTransitionIndex; i < transitions.Count; i++)
                {
                    var transition = transitions[i];
                    transition.StartKeyFrame.Number = (i * 2) + 1;
                    transition.EndKeyFrame.Number = (i * 2) + 2;
                    var startProps = frameProps[keyFrameToElement[transition.StartKeyFrame]];
                    if (!startProps.KeyFrameLabel.HoldsTwo) startProps.KeyFrameLabel.Number = transition.StartKeyFrame.Number;
                    var endProps = frameProps[keyFrameToElement[transition.EndKeyFrame]];
                    endProps.KeyFrameLabel.Number = transition.EndKeyFrame.Number;
                }
            }
        }

        private GridView AddMultiKeyFrameLabel(int x, int y, params KeyFrameLabel[] labels)
        {
            var multiKeyFrameLabel = (GridView)multiKeyFrameLabelTemplate.LoadContent();
            ContentCanvas.Children.Add(multiKeyFrameLabel);
            Canvas.SetLeft(multiKeyFrameLabel, x);
            Canvas.SetTop(multiKeyFrameLabel, y);
            multiKeyFrameLabel.ItemsSource = new ObservableCollection<KeyFrameLabel>(labels);
            return multiKeyFrameLabel;
        }

        private void CanvasAddKeyFrameClicked(object sender, RoutedEventArgs e)
        {
            var lastKeyFrame = transitions.Last().EndKeyFrame;
            var startFrame = new KeyFrame(lastKeyFrame.X, lastKeyFrame.Y, lastKeyFrame.Width, lastKeyFrame.Height, lastKeyFrame.Number + 1);
            var startKeyFrameElement = keyFrameToElement[lastKeyFrame];
            var lastKeyFrameProps = frameProps[startKeyFrameElement];
            lastKeyFrameProps.KeyFrameLabel.HoldsTwo = true;
            lastKeyFrameProps.KeyFrame2 = startFrame;
            keyFrameToElement.Add(startFrame, startKeyFrameElement);
            var endKeyFrameElement = AddNewKeyFrameElement(startFrame.X, startFrame.Y, startFrame.Width, startFrame.Height);// Add it at startFrame position...
            resizer.PositionElement(endKeyFrameElement, _lastCanvasPressedPoint.X, _lastCanvasPressedPoint.Y);// ...then move it to intended position to keep it in bounds
            var endFrame = new KeyFrame((int)resizer.GetElementLeft(endKeyFrameElement), (int)resizer.GetElementTop(endKeyFrameElement),
                startFrame.Width, startFrame.Height, lastKeyFrame.Number + 2);
            keyFrameToElement.Add(endFrame, endKeyFrameElement);
            var endKeyFrameProps = new KeyFrameProps
            {
                KeyFrameLabel = new KeyFrameLabel(endFrame.Number),
                KeyFrame = endFrame,
            };
            endKeyFrameElement.DataContext = endKeyFrameProps.KeyFrameLabel;
            frameProps.Add(endKeyFrameElement, endKeyFrameProps);
            LinkKeyFrameElements(startKeyFrameElement, lastKeyFrameProps, endKeyFrameElement, endKeyFrameProps);
            transitions.Add(new Transition
            {
                StartKeyFrame = startFrame,
                EndKeyFrame = endFrame,
                Duration = TimeSpan.FromSeconds(defaultDurationInSeconds)
            });
        }

        private void CanvasContainer_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            CanvasContainer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, CanvasContainer.ActualWidth, CanvasContainer.ActualHeight)
            };
        }

        private void CanvasAddTransitionClicked(object sender, RoutedEventArgs e)
        {
            var lastKeyFrame = transitions.Last().EndKeyFrame;
            var dummyFrame = AddNewKeyFrameElement(lastKeyFrame.X, lastKeyFrame.Y, lastKeyFrame.Width, lastKeyFrame.Height);// Add it at lastKeyFrame position...
            resizer.PositionElement(dummyFrame, _lastCanvasPressedPoint.X, _lastCanvasPressedPoint.Y);// ...then move it to intended position to keep it in bounds
            var (left, top) = (resizer.GetElementLeft(dummyFrame), resizer.GetElementTop(dummyFrame));// Get the new position after moving...
            resizer.RemoveElement(dummyFrame);// ...and remove the dummy frame
            AddTransitionKeyFrames((int)left, (int)top, lastKeyFrame.Width, lastKeyFrame.Height);
        }

        private void CanvasFitToViewClicked(object sender, RoutedEventArgs e)
        {
            FitCanvasToView();
        }

        private void FrameHighlightClicked(object sender, RoutedEventArgs e)
        {
            var menuItem = (ToggleMenuFlyoutItem)sender;
            var keyframe = (KeyFrame)menuItem.DataContext;
            keyframe.Highlighted = menuItem.IsChecked;
            frameProps[keyFrameToElement[keyframe]].KeyFrameLabel.Highlighted = menuItem.IsChecked;
        }

        private void FrameBringToTop(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuFlyoutItem)sender;
            var keyframe = (KeyFrame)menuItem.DataContext;
            var keyframeElement = keyFrameToElement[keyframe];
            resizer.SetElementZIndexTopmost(keyframeElement);
            var keyframeProps = frameProps[keyframeElement];
            if (!keyframeProps.KeyFrameLabel.PartOfClump) return;
            var clumpLabels = (ObservableCollection<KeyFrameLabel>)keyframeProps.ClumpGridView!.ItemsSource;
            var labelIndex = clumpLabels.IndexOf(keyframeProps.KeyFrameLabel);
            clumpLabels.Move(labelIndex, clumpLabels.Count - 1);
        }

        private void FrameCenterInViewClicked(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuFlyoutItem)sender;
            var keyframe = (KeyFrame)menuItem.DataContext;
            const int margin = 100;
            if (CanvasContainer.ActualWidth / CanvasContainer.ActualHeight < (double)keyframe.Width / keyframe.Height)
            {
                ZoomTransform.ScaleX = ZoomTransform.ScaleY = CanvasContainer.ActualWidth / (keyframe.Width + margin * 2);
                PanTransform.X = -(keyframe.X - margin) * ZoomTransform.ScaleX;
                PanTransform.Y = -(keyframe.Y * ZoomTransform.ScaleY - (CanvasContainer.ActualHeight - keyframe.Height * ZoomTransform.ScaleY) / 2);
            }
            else
            {
                ZoomTransform.ScaleX = ZoomTransform.ScaleY = CanvasContainer.ActualHeight / (keyframe.Height + margin * 2);
                PanTransform.Y = -(keyframe.Y - margin) * ZoomTransform.ScaleY;
                PanTransform.X = -(keyframe.X * ZoomTransform.ScaleX - (CanvasContainer.ActualWidth - keyframe.Width * ZoomTransform.ScaleX) / 2);
            }
        }

        private void FrameHideClicked(object sender, RoutedEventArgs e)
        {
            var menuItem = (ToggleMenuFlyoutItem)sender;
            var keyframe = (KeyFrame)menuItem.DataContext;
            var keyframeElement = keyFrameToElement[keyframe];
            var props = frameProps[keyframeElement];
            if (!menuItem.IsChecked)
            {
                resizer.SetElementZIndex(keyframeElement, frameProps[keyframeElement].zIndexBeforeHide);
                props.KeyFrameLabel.Hidden = false;
            }
            else
            {
                props.zIndexBeforeHide = resizer.GetElementZIndex(keyframeElement);
                props.KeyFrameLabel.Hidden = true;
                resizer.SetElementZIndex(keyframeElement, -1); // Behind media element
            }
        }

        private void FrameXChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (userIsHandlingFrames || double.IsNaN(args.OldValue)) return;
            var keyframe = (KeyFrame)sender.DataContext;
            if (keyframe.X == (int)args.NewValue) return;
            keyframe.X = (int)args.NewValue;
            var keyframeElement = keyFrameToElement[keyframe];
            resizer.PositionElementLeft(keyframeElement, args.NewValue);
            UpdateAnimLinesAndCoords(keyframeElement);
            CheckClumps(keyframeElement);
            sender.Value = keyframe.X;
        }

        private void FrameYChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (userIsHandlingFrames || double.IsNaN(args.OldValue)) return;
            var keyframe = (KeyFrame)sender.DataContext;
            if (keyframe.Y == (int)args.NewValue) return;
            keyframe.Y = (int)args.NewValue;
            var keyframeElement = keyFrameToElement[keyframe];
            resizer.PositionElementTop(keyframeElement, args.NewValue);
            UpdateAnimLinesAndCoords(keyframeElement);
            CheckClumps(keyframeElement);
            sender.Value = keyframe.Y;
        }

        private void FrameWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (userIsHandlingFrames || double.IsNaN(args.OldValue)) return;
            var keyframe = (KeyFrame)sender.DataContext;
            if (keyframe.Width == (int)args.NewValue) return;
            keyframe.Width = (int)args.NewValue;
            var keyframeElement = keyFrameToElement[keyframe];
            resizer.ResizeElementWidth(keyframeElement, args.NewValue);
            UpdateAnimLinesAndCoords(keyframeElement);
            CheckAspectRatio(keyframeElement);
            sender.Value = keyframe.Width;
        }

        private void FrameHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (userIsHandlingFrames || double.IsNaN(args.OldValue)) return;
            var keyframe = (KeyFrame)sender.DataContext;
            if (keyframe.Height == (int)args.NewValue) return;
            keyframe.Height = (int)args.NewValue;
            var keyframeElement = keyFrameToElement[keyframe];
            resizer.ResizeElementHeight(keyframeElement, args.NewValue);
            UpdateAnimLinesAndCoords(keyframeElement);
            CheckAspectRatio(keyframeElement);
            sender.Value = keyframe.Height;
        }

        private void LockAspectRatioChanged(object sender, RoutedEventArgs e)
        {
            foreach (var frameElement in frameProps.Keys)
            {
                resizer.SetNewHandlingParameters(frameElement, GetAspectRatioParam(LockAspectRatioCheckBox.IsChecked ?? false));
            }
        }

        private async void ProcessTour(object sender, RoutedEventArgs e)
        {
            var transitionsToProcess = transitions.Select(t => new ImageTourProcessor.Transition
            {
                StartKeyFrame = new ImageTourProcessor.KeyFrame { X = t.StartKeyFrame.X, Y = t.StartKeyFrame.Y, Width = t.StartKeyFrame.Width, Height = t.StartKeyFrame.Height },
                EndKeyFrame = new ImageTourProcessor.KeyFrame { X = t.EndKeyFrame.X, Y = t.EndKeyFrame.Y, Width = t.EndKeyFrame.Width, Height = t.EndKeyFrame.Height },
                Duration = t.Duration
            }).ToArray();
            viewModel.State = OperationState.DuringOperation;

            var lastTransition = 0;
            var progress = new Progress<ImageTourProcessor.ValueProgress>(progress =>
            {
                if (progress.CurrentTransition > transitionsToProcess.Length)
                {
                    CurrentTransitionLabel.Text = string.Empty;
                    CurrentStatus.Text = "Merging frames...";
                    CurrentTourProgress.Value = (double)progress.CurrentFrame / progress.TotalFrames * progressMax;
                    CurrentTourProgressText.Text = $"{progress.CurrentFrame} / {progress.TotalFrames}";
                    return;
                }
                OverallTourProgress.Value = (double)progress.CurrentFrame / progress.TotalFrames * progressMax;
                OverallTourProgressText.Text = $"{progress.CurrentFrame} / {progress.TotalFrames}";
                CurrentTourProgress.Value = (double)progress.CurrentTransitionFrame / progress.TotalTransitionFrames * progressMax;
                CurrentTourProgressText.Text = $"{progress.CurrentTransitionFrame} / {progress.TotalTransitionFrames}";
                if (lastTransition != progress.CurrentTransition)
                {
                    CurrentStatus.Text = $"{progress.CurrentTransition} / {transitionsToProcess.Length}";
                    var startNum = progress.CurrentTransition * 2 - 1;
                    CurrentTransitionLabel.Text = $"{startNum} -> {startNum + 1}";
                    lastTransition = progress.CurrentTransition;
                    CurrentStatus.Text = "Generating frames...";
                }

            });

            try
            {
                var payload = await tourProcessor.Animate(mediaPath, isVideo, (int)OutputWidth.Value, (int)OutputHeight.Value, OutputFrameRate.Value,
                    transitionsToProcess, SetOutputFile, progress, DontDeleteFrames.IsChecked ?? false);

                if (viewModel.State == OperationState.BeforeOperation) return; //Canceled
                if (!payload.Success)
                {
                    viewModel.State = OperationState.BeforeOperation;
                    await ErrorAction(payload.ErrorMessage);
                    //await tourProcessor.Cancel(outputFile);
                    return;
                }

                viewModel.State = OperationState.AfterOperation;
                CurrentStatus.Text = "Done";
            }
            catch (Exception ex)
            {
                await ErrorAction(ex.Message);
                viewModel.State = OperationState.BeforeOperation;
            }

            void SetOutputFile(string file)
            {
                outputFile = file;
            }

            async Task ErrorAction(string message)
            {
                ErrorDialog.Title = "Tour operation failed";
                ErrorDialog.Content = message;
                await ErrorDialog.ShowAsync();
            }
        }

        private void PauseOrViewTour_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                tourProcessor.ViewFile(outputFile);
                return;
            }

            if (viewModel.ProcessPaused)
            {
                tourProcessor.Resume();
                viewModel.ProcessPaused = false;
            }
            else
            {
                tourProcessor.Pause();
                viewModel.ProcessPaused = true;
            }
        }

        private void CancelOrClose_OnClick(object sender, RoutedEventArgs e)
        {
            if (viewModel.State == OperationState.AfterOperation)
            {
                viewModel.State = OperationState.BeforeOperation;
                return;
            }

            FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
        }

        private async void CancelProcess(object sender, RoutedEventArgs e)
        {
            await tourProcessor.Cancel(outputFile);
            viewModel.State = OperationState.BeforeOperation;
            viewModel.ProcessPaused = false;
            CancelFlyout.Hide();
        }

        private void GoBack(object sender, RoutedEventArgs e)
        {
            if(isVideo) Video.MediaPlayer.Pause();
            _ = tourProcessor.Cancel(outputFile);
            if (navigateTo == null) Frame.GoBack();
            else Frame.NavigateToType(Type.GetType(navigateTo), outputFile, new FrameNavigationOptions { IsNavigationStackEnabled = false });
        }

        private void OutputSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            foreach (var keyframeElement in frameProps.Keys)
            {
                CheckAspectRatio(keyframeElement);
            }
        }
    }

    class AnimLines
    {
        public Line TopLeftLine { get; set; }
        public Line TopRightLine { get; set; }
        public Line BottomLeftLine { get; set; }
        public Line BottomRightLine { get; set; }
    }

    class KeyFrameProps
    {
        public (AnimLines? From, AnimLines? To) AnimLines { get; set; }
        public KeyFrameLabel KeyFrameLabel { get; set; }
        public KeyFrame KeyFrame { get; set; }
        public KeyFrame? KeyFrame2 { get; set; }
        public GridView? ClumpGridView { get; set; }
        public int zIndexBeforeHide { get; set; }
    }

    public class TourProps
    {
        public string FfmpegPath { get; set; }
        public string MediaPath { get; set; }
        public string? TypeToNavigateTo { get; set; }
    }
}
