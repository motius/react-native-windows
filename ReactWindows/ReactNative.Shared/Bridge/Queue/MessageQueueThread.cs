﻿using ReactNative.Common;
using ReactNative.Tracing;
using System;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using static System.FormattableString;
#if WINDOWS_UWP
using Windows.ApplicationModel.Core;
#else
using System.Windows.Threading;
#endif

namespace ReactNative.Bridge.Queue
{
    /// <summary>
    /// Encapsulates an action queue.
    /// </summary>
    public abstract class MessageQueueThread : IMessageQueueThread, IDisposable
    {
        private readonly string _name;

        private int _disposed;

        private MessageQueueThread(string name)
        {
            _name = name;
        }

        /// <summary>
        /// Flags if the <see cref="MessageQueueThread"/> is disposed.
        /// </summary>
        protected bool IsDisposed
        {
            get
            {
                return _disposed > 0;
            }
        }

        /// <summary>
        /// Checks if the caller is running on the queue instance.
        /// </summary>
        /// <returns>
        /// <b>true</b> if the caller is calling from the queue, <b>false</b>
        /// otherwise.
        /// </returns>
        public bool IsOnThread()
        {
            return IsOnThreadCore();
        }

        /// <summary>
        /// Queues an action to run.
        /// </summary>
        /// <param name="action">The action.</param>
        public void RunOnQueue(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (IsDisposed)
            {
                Tracer.Write(ReactConstants.Tag, Invariant($"Dropping enqueued action on disposed '{_name}' thread."));
                return;
            }

            Enqueue(action);
        }

        /// <summary>
        /// Disposes the action queue.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Enqueues the action.
        /// </summary>
        /// <param name="action">The action.</param>
        protected abstract void Enqueue(Action action);

        /// <summary>
        /// Checks if the caller is running on the queue instance.
        /// </summary>
        /// <returns>
        /// <b>true</b> if the caller is calling from the queue, <b>false</b>
        /// otherwise.
        /// </returns>
        protected abstract bool IsOnThreadCore();

        /// <summary>
        /// Disposes the action queue.
        /// </summary>
        /// <param name="disposing">
        /// <b>false</b> if dispose was triggered by a finalizer, <b>true</b>
        /// otherwise.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposed);
            }
        }

        /// <summary>
        /// Factory to create the action queue.
        /// </summary>
        /// <param name="spec">The action queue specification.</param>
        /// <param name="handler">The exception handler.</param>
        /// <returns>The action queue instance.</returns>
        public static MessageQueueThread Create(
            MessageQueueThreadSpec spec,
            Action<Exception> handler)
        {
            if (spec == null)
                throw new ArgumentNullException(nameof(spec));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            switch (spec.Kind)
            {
                case MessageQueueThreadKind.DispatcherThread:
                case MessageQueueThreadKind.LayoutThread:
                    var isSecondary = spec.Kind == MessageQueueThreadKind.LayoutThread;
                    return new DispatcherMessageQueueThread(spec.Name, handler, isSecondary);
                case MessageQueueThreadKind.BackgroundSingleThread:
                    return new SingleBackgroundMessageQueueThread(spec.Name, handler);
                case MessageQueueThreadKind.BackgroundAnyThread:
                    return new AnyBackgroundMessageQueueThread(spec.Name, handler);
                default:
                    throw new InvalidOperationException(
                        Invariant($"Unknown thread type '{spec.Kind}' with name '{spec.Name}'."));
            }
        }

        class DispatcherMessageQueueThread : MessageQueueThread
        {
#if WINDOWS_UWP && CREATE_LAYOUT_THREAD
            private static readonly CoreApplicationView s_layoutApplicationView = CoreApplication.CreateNewView();
#endif
            private static readonly IObserver<Action> s_nop = Observer.Create<Action>(_ => { });

            private readonly bool _isSecondary;
            private readonly Subject<Action> _actionSubject;
            private readonly IDisposable _subscription;

#if WINDOWS_UWP
            private readonly CoreApplicationView _currentApplicationView;
#else
            private readonly DispatcherManager _layoutDispatcherManager;
            private readonly Thread _currentDispatcherThread;
#endif

            private IObserver<Action> _actionObserver;

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
            public DispatcherMessageQueueThread(string name, Action<Exception> handler, bool isSecondary)
                : base(name)
            {
                _isSecondary = isSecondary;
                _actionSubject = new Subject<Action>();
                _actionObserver = _actionSubject;

#if WINDOWS_UWP
#if CREATE_LAYOUT_THREAD                
                _currentApplicationView = isSecondary
                    ? s_layoutApplicationView
                    : CoreApplication.GetCurrentView();
#else
                // For DEBUG builds, we use the main UI dispatcher fors both
                // layout and dispatcher queue threads because of a limitation
                // with the native debugging capabilities.
                _currentApplicationView = CoreApplication.GetCurrentView();
#endif
                var dispatcher = _currentApplicationView.Dispatcher;
#else
                if (_isSecondary)
                {
                    _layoutDispatcherManager = new DispatcherManager();
                }

                var dispatcher = isSecondary
                    ? _layoutDispatcherManager.DispatcherInstance
                    : Dispatcher.CurrentDispatcher;

                _currentDispatcherThread = dispatcher.Thread;
#endif

                _subscription = _actionSubject
                    .ObserveOn(dispatcher)
                    .Subscribe(action =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            handler(ex);
                        }
                    });
            }

            protected override void Enqueue(Action action)
            {
                _actionObserver.OnNext(action);
            }

            protected override bool IsOnThreadCore()
            {
#if WINDOWS_UWP
                return GetApplicationView() == _currentApplicationView;
#else
                return Thread.CurrentThread == _currentDispatcherThread;
#endif
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                Interlocked.Exchange(ref _actionObserver, s_nop);
                _actionSubject.Dispose();
                _subscription.Dispose();
#if !WINDOWS_UWP
                if (_isSecondary)
                {
                    _layoutDispatcherManager.Dispose();
                }
#endif
            }

#if WINDOWS_UWP
            private CoreApplicationView GetApplicationView()
            {
#if CREATE_LAYOUT_THREAD
                if (_isSecondary && s_layoutApplicationView.Dispatcher.HasThreadAccess)
                {
                    return s_layoutApplicationView;
                }
                else if (!_isSecondary)
                {
                    return CoreApplication.GetCurrentView();
                }

                return null;
#else
                return CoreApplication.GetCurrentView();
#endif
            }
#endif

#if !WINDOWS_UWP
            class DispatcherManager : IDisposable
            {
                public DispatcherManager()
                {
                    var ready = new ManualResetEvent(false);
                    ThreadInstance = new Thread(new ThreadStart(() => {
                        DispatcherInstance = Dispatcher.CurrentDispatcher;
                        ready.Set();
                        Dispatcher.Run();
                    }));

                    ThreadInstance.SetApartmentState(ApartmentState.STA);
                    ThreadInstance.Start();
                    ready.WaitOne();
                }

                public Dispatcher DispatcherInstance
                {
                    get;
                    private set;
                }

                public Thread ThreadInstance
                {
                    get;
                }

                public void Dispose()
                {
                    DispatcherInstance.InvokeShutdown();
                    ThreadInstance.Join();
                }
            }
#endif
        }

        class SingleBackgroundMessageQueueThread : MessageQueueThread
        {
            private static readonly Action s_canary = new Action(() => { });

            private readonly Action<Exception> _handler;
            private readonly BlockingCollection<Action> _queue;
            private readonly ThreadLocal<bool> _indicator;
            private readonly ManualResetEvent _doneHandle;

            public SingleBackgroundMessageQueueThread(string name, Action<Exception> handler)
                : base(name)
            {
                _handler = handler;
                _queue = new BlockingCollection<Action>();
                _indicator = new ThreadLocal<bool>();
                _doneHandle = new ManualResetEvent(false);
                Task.Run(() =>
                {
                    _indicator.Value = true;
                    Run();
                    _doneHandle.Set();
                });
            }

            protected override bool IsOnThreadCore()
            {
                return _indicator.Value;
            }

            protected override void Enqueue(Action action)
            {
                _queue.Add(action);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);

                // Unblock the background thread.
                Enqueue(s_canary);

                _doneHandle.WaitOne();
                _doneHandle.Dispose();
                _indicator.Dispose();
                _queue.Dispose();
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
            private void Run()
            {
                while (true)
                {
                    var action = _queue.Take();
                    if (IsDisposed)
                    {
                        break;
                    }

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _handler(ex);
                    }
                }
            }
        }

        class AnyBackgroundMessageQueueThread : MessageQueueThread
        {
            private readonly object _gate = new object();

            private readonly Action<Exception> _handler;
            private readonly TaskScheduler _taskScheduler;
            private readonly TaskFactory _taskFactory;

            public AnyBackgroundMessageQueueThread(string name, Action<Exception> handler)
                : base(name)
            {
                _handler = handler;
                _taskScheduler = new LimitedConcurrencyLevelTaskScheduler(1);
                _taskFactory = new TaskFactory(_taskScheduler);
            }

            protected override async void Enqueue(Action action)
            {
                await _taskFactory.StartNew(() =>
                {
                    try
                    {
                        lock (_gate)
                        {
                            if (!IsDisposed)
                            {
                                action();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _handler(ex);
                    }
                }).ConfigureAwait(false);
            }

            protected override void Dispose(bool disposing)
            {
                // Warning: will deadlock if disposed from own queue thread.
                lock (_gate)
                {
                    base.Dispose(disposing);
                }
            }

            protected override bool IsOnThreadCore()
            {
                return TaskScheduler.Current == _taskScheduler;
            }
        }
    }
}
