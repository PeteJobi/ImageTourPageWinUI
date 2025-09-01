## Image Tour Page WinUI 3
This provides a reuseable WinUI 3 page with an interface that allows for touring images to create smaller frame videos from higher resolution images. Also supports video inputs.

<img width="1787" height="1014" alt="Screenshot 2025-09-01 163707" src="https://github.com/user-attachments/assets/0a467080-326a-451e-8903-86e8fb08fe9e" />

# How to use
This library depends on [DraggerResizerWinUI](https://github.com/PeteJobi/DraggerResizerWinUI). Include both libraries into your WinUI solution and reference them in your WinUI project. Then navigate to the **ImageTourPage** when the user requests for it, passing a **TourProps** object as parameter.
The **TourProps** object should contain the path to ffmpeg, the path to the media file, and optionally, the full name of the Page type to navigate back to when the user is done. If this last parameter is provided, you can get the path to the file that was generated on the Image Tour page. If not, the user will be navigated back to whichever page called the Image Tour page and there'll be no parameters. 
```
private void GoToTour(){
  var ffmpegPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/ffmpeg.exe");
  var mediaPath = Path.Join(Package.Current.InstalledLocation.Path, "Assets/image.png");
  Frame.Navigate(typeof(ImageTourPage), new TourProps { FfmpegPath = ffmpegPath, MediaPath = mediaPath, TypeToNavigateTo = typeof(MainPage).FullName });
}

protected override void OnNavigatedTo(NavigationEventArgs e)
{
    //outputFile is sent only if TypeToNavigateTo was specified in TourProps.
    if (e.Parameter is string outputFile)
    {
        Console.WriteLine($"Path to the toured file is {outputFile}");
    }
}
```

You may check out [ImageTour](https://github.com/PeteJobi/ImageTour) to see a full application that uses this page.
