using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace ImageTourPage
{
    public class TourMainModel: INotifyPropertyChanged
    {
        private bool _isplaying;
        public bool IsPlaying
        {
            get => _isplaying;
            set => SetProperty(ref _isplaying, value);
        }
        private OperationState _state;
        public OperationState State
        {
            get => _state;
            set => SetProperty(ref _state, value, alsoNotify: [nameof(BeforeOperation), nameof(DuringOperation), nameof(AfterOperation)]);
        }

        private bool _processpaused;
        public bool ProcessPaused
        {
            get => _processpaused;
            set => SetProperty(ref _processpaused, value);
        }

        public bool BeforeOperation => State == OperationState.BeforeOperation;
        public bool DuringOperation => State == OperationState.DuringOperation;
        public bool AfterOperation => State == OperationState.AfterOperation;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }
    }
    public class KeyFrame(int x, int y, int width, int height, int number) : INotifyPropertyChanged
    {
        private int _x = x;
        public int X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }
        private int _y = y;
        public int Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }
        private int _width = width;
        public int Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }
        private int _height = height;
        public int Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }
        private int _number = number;
        public int Number
        {
            get => _number;
            set => SetProperty(ref _number, value, alsoNotify: nameof(IsStartKeyframe));
        }
        private bool _highlighted;
        public bool Highlighted
        {
            get => _highlighted;
            set => SetProperty(ref _highlighted, value);
        }
        private bool _incorrectAspectRatio;
        public bool IncorrectAspectRatio
        {
            get => _incorrectAspectRatio;
            set => SetProperty(ref _incorrectAspectRatio, value);
        }

        public bool IsStartKeyframe => Number % 2 == 1;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }

        public override string ToString()
        {
            return $"({X}, {Y}, {Width}, {Height})";
        }
    }

    public class Transition : INotifyPropertyChanged
    {
        private KeyFrame _startkeyframe;
        public KeyFrame StartKeyFrame
        {
            get => _startkeyframe;
            set => SetProperty(ref _startkeyframe, value);
        }
        private KeyFrame _endkeyframe;
        public KeyFrame EndKeyFrame
        {
            get => _endkeyframe;
            set => SetProperty(ref _endkeyframe, value);
        }
        private TimeSpan _duration;
        public TimeSpan Duration
        {
            get => _duration;
            set => SetProperty(ref _duration, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public override string ToString()
        {
            return $@"{StartKeyFrame} -> {EndKeyFrame} <{Duration:hh\:mm\:ss\.fff}>";
        }
    }

    public class KeyFrameLabel(int number, bool holdstwo = false, bool partOfClump = false) : INotifyPropertyChanged
    {
        private int _number = number;
        public int Number
        {
            get => _number;
            set => SetProperty(ref _number, value, alsoNotify: nameof(LabelDisplay));
        }
        private bool _holdstwo = holdstwo;
        public bool HoldsTwo
        {
            get => _holdstwo;
            set => SetProperty(ref _holdstwo, value, alsoNotify: nameof(LabelDisplay));
        }
        private bool _partOfClump = partOfClump;
        public bool PartOfClump
        {
            get => _partOfClump;
            set => SetProperty(ref _partOfClump, value);
        }
        private bool _highlighted;
        public bool Highlighted
        {
            get => _highlighted;
            set => SetProperty(ref _highlighted, value);
        }

        public string LabelDisplay => HoldsTwo ? $"{Number},{Number + 1}" : Number.ToString();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }
    }

    public enum OperationState
    {
        BeforeOperation, DuringOperation, AfterOperation
    }
}
