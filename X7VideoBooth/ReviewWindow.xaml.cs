using System;
using System.ComponentModel; // for CancelEventArgs
using System.Windows;

namespace X7VideoBooth
{
    public partial class ReviewWindow : Window
    {
        private readonly Uri _source;

        // Choose your default when the user clicks ✕:
        // true  => Keep (default your current behavior)
        // false => Retake
        private readonly bool _defaultOnClose = true;

        public ReviewWindow(string outputPath)
        {
            InitializeComponent();
            _source = new Uri(outputPath);

            Loaded += (_, __) =>
            {
                // Load & start playback when the visual tree is ready
                mediaPlayer.Source = _source;
                mediaPlayer.Position = TimeSpan.Zero;
                mediaPlayer.Play();
            };
        }

        private void keepBtn_Click(object sender, RoutedEventArgs e)
        {
            // Setting DialogResult automatically closes the dialog
            DialogResult = true;
        }

        private void reBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void againBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                mediaPlayer.Position = TimeSpan.Zero;
                mediaPlayer.Play();
            }
            catch { /* ignore */ }
        }

        // IMPORTANT: use Closing, not Closed.
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // Release file lock
            try { mediaPlayer.Stop(); } catch { }
            try { mediaPlayer.Source = null; } catch { }

            // If user used the titlebar ✕, DialogResult hasn't been set yet.
            // Only valid when window was shown with ShowDialog().
            if (!DialogResult.HasValue)
                DialogResult = _defaultOnClose;
        }
    }
}
