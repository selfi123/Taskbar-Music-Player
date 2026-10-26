using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace TaskbarMusicApp
{
    public partial class MainWindow : Window
    {
        private readonly YoutubeClient youtube = new YoutubeClient();
        private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "TaskbarMusicApp_Cache");
        private CancellationTokenSource? playbackCts;
        private bool isPlaying = false;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                Directory.CreateDirectory(CacheDirectory);
            }
            catch { }
        }

        // 1. Position Window on the right side, slightly above the Taskbar
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Give it a fixed width instead of taking the whole screen
            this.Width = 600; 
            
            // Calculate taskbar height, default to 48 if it can't be determined
            double taskbarHeight = (screenHeight - workArea.Height) > 0 ? (screenHeight - workArea.Height) : 48;
            this.Height = taskbarHeight;

            // Place on the right end (with a 20px margin from the right edge)
            this.Left = workArea.Right - this.Width - 20;
            
            // Lift it up (20 pixels above the taskbar)
            this.Top = workArea.Bottom - this.Height - 20;

            _ = LoadPopularSingers();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            playbackCts?.Cancel();
            AudioPlayer.Stop();
            AudioPlayer.Close();
            this.Close();
        }

        private async System.Threading.Tasks.Task LoadPopularSingers()
        {
            SingersDropdown.ItemsSource = new List<string>
            {
                "Bruno Mars",
                "Ariana Grande",
                "Taylor Swift",
                "The Weeknd",
                "Drake",
                "Ed Sheeran",
                "Justin Bieber",
                "Dua Lipa",
                "Billie Eilish",
                "Post Malone"
            };
            SingersDropdown.SelectedIndex = -1;
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private async void SingersDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SingersDropdown.SelectedItem is string selectedArtist)
            {
                try
                {
                    SingersDropdown.IsEnabled = false;
                    TrackInfoText.Text = $"🔍 Searching {selectedArtist}...";
                    TrackInfoText.Foreground = System.Windows.Media.Brushes.Yellow;
                    var tracks = new List<Track>();
                    int count = 0;
                    
                    // Search for "{artist} songs" on YouTube
                    await foreach (var video in youtube.Search.GetVideosAsync(selectedArtist + " songs"))
                    {
                        tracks.Add(new Track
                        {
                            Name = video.Title,
                            Artist = video.Author.ChannelTitle,
                            AudioUrl = video.Id.Value // Store YouTube Video ID here
                        });
                        count++;
                        if (count >= 20) break;
                    }

                    ResultsDropdown.ItemsSource = tracks;
                    ResultsDropdown.Visibility = Visibility.Visible;
                    ResultsDropdown.IsDropDownOpen = true;
                    TrackInfoText.Text = $"Select a song by {selectedArtist}";
                    TrackInfoText.Foreground = System.Windows.Media.Brushes.LightGray;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Search failed: " + ex.Message);
                }
                finally
                {
                    SingersDropdown.IsEnabled = true;
                }
            }
        }

        // 3. Play Selected Track (Streams/Downloads Audio safely to local cache for WPF MediaElement)
        private async void ResultsDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsDropdown.SelectedItem is Track selectedTrack)
            {
                playbackCts?.Cancel();
                playbackCts = new CancellationTokenSource();
                var token = playbackCts.Token;

                string cleanId = string.Join("_", selectedTrack.AudioUrl.Split(Path.GetInvalidFileNameChars()));
                string cacheFilePath = Path.Combine(CacheDirectory, $"{cleanId}.m4a");
                string tempDownloadPath = cacheFilePath + ".part";

                try
                {
                    // If already downloaded and cached, play immediately
                    if (File.Exists(cacheFilePath) && new FileInfo(cacheFilePath).Length > 1024)
                    {
                        TrackInfoText.Text = $"🎵 {selectedTrack.Name} - {selectedTrack.Artist}";
                        TrackInfoText.Foreground = System.Windows.Media.Brushes.LightGreen;
                        AudioPlayer.Source = new Uri(cacheFilePath, UriKind.Absolute);
                        AudioPlayer.Play();
                        isPlaying = true;
                        PlayPauseBtn.Content = "⏸";
                        return;
                    }

                    TrackInfoText.Text = $"⏳ Fetching stream: {selectedTrack.Name}...";
                    TrackInfoText.Foreground = System.Windows.Media.Brushes.LightSkyBlue;

                    var streamManifest = await youtube.Videos.Streams.GetManifestAsync(selectedTrack.AudioUrl, token);
                    
                    // Prefer MP4 audio stream for native Windows / WPF decoding
                    var audioStreams = streamManifest.GetAudioOnlyStreams();
                    var streamInfo = audioStreams.Where(s => s.Container == Container.Mp4).GetWithHighestBitrate()
                                     ?? audioStreams.GetWithHighestBitrate();

                    if (streamInfo == null)
                    {
                        throw new InvalidOperationException("No audio streams available for this track.");
                    }

                    var progress = new Progress<double>(p =>
                    {
                        TrackInfoText.Text = $"⏳ Buffering ({p:P0}): {selectedTrack.Name}";
                    });

                    // Download to temporary partial file first
                    await youtube.Videos.Streams.DownloadAsync(streamInfo, tempDownloadPath, progress, token);

                    if (File.Exists(cacheFilePath))
                    {
                        File.Delete(cacheFilePath);
                    }
                    File.Move(tempDownloadPath, cacheFilePath);

                    // Local file URI works 100% reliably in WPF MediaElement without pack URI security issues
                    AudioPlayer.Source = new Uri(cacheFilePath, UriKind.Absolute);
                    AudioPlayer.Play();
                    isPlaying = true;
                    PlayPauseBtn.Content = "⏸";
                    TrackInfoText.Text = $"🎵 {selectedTrack.Name} - {selectedTrack.Artist}";
                    TrackInfoText.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                catch (OperationCanceledException)
                {
                    // User selected another song before download finished
                    if (File.Exists(tempDownloadPath))
                    {
                        try { File.Delete(tempDownloadPath); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    isPlaying = false;
                    PlayPauseBtn.Content = "▶";
                    TrackInfoText.Text = "Playback failed";
                    TrackInfoText.Foreground = System.Windows.Media.Brushes.IndianRed;
                    MessageBox.Show("Playback failed: " + ex.Message, "Music Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // 4. Play / Pause Toggle
        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AudioPlayer.Source == null) return;

            if (isPlaying)
            {
                AudioPlayer.Pause();
                PlayPauseBtn.Content = "▶";
            }
            else
            {
                AudioPlayer.Play();
                PlayPauseBtn.Content = "⏸";
            }
            isPlaying = !isPlaying;
        }

        private void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            isPlaying = false;
            PlayPauseBtn.Content = "▶";
        }

        private void AudioPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            isPlaying = false;
            PlayPauseBtn.Content = "▶";
            TrackInfoText.Text = "Playback error";
            TrackInfoText.Foreground = System.Windows.Media.Brushes.IndianRed;
            MessageBox.Show("Media playback error: " + e.ErrorException?.Message, "Player Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public class Track
    {
        public string Name { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AudioUrl { get; set; } = string.Empty;
    }
}