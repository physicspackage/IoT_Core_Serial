using Falafel.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace IoTCoreSerial.Classes
{
    public class ESPViewModel : ViewModelBase
    {
        SerialDevice serialPort;
        DataReader dataReaderObject = null;
        CancellationTokenSource ReadCancellationTokenSource;
        string[] split = new string[1] { Environment.NewLine };
        const string port = "TTL232R-3V3";
        const uint ReadBufferLength = 1024;

        Action<object> returnAction = null;
        bool disconnectAfterAction = false;
        string trigger = string.Empty;
        Action<object> triggerAction = null;
        Action<object> sendAction = null;
        DispatcherTimer timeoutTimer;

        public ESPViewModel()
        {
            timeoutTimer = new DispatcherTimer();
            _Connect = new DelegateCommand(async (x) =>
            {
                try
                {
                    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        this.Status = "Opening " + port;
                    });
                    await ConnectToPort();

                    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        this.Status = port + " Opened";
                        RaisePropertyChangedEventHandlers();
                        if (x is Action<object> && x != null)
                        {
                            (x as Action<object>)(true);
                        }
                    });
                }
                catch(Exception ex)
                {
                    if (x is Action<object> && x != null)
                    {
                        (x as Action<object>)(false);
                    }
                    await PostException(ex);
                }
            }, (y) => { return !Connected(); });

            _Disconnect = new DelegateCommand(async (x) =>
            {
                await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    this.Status = "Closing" + port;
                });
                try
                {
                    CancelReadTask();
                    CloseDevice();
                    ReceivedText = string.Empty;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        this.Status = "UART0 Closed";
                        RaisePropertyChangedEventHandlers();
                        if (x is Action<object> && x != null)
                        {
                            (x as Action<object>)(true);
                        }
                    });
                }
                catch (Exception ex)
                {
                    if (x is Action<object> && x != null)
                    {
                        (x as Action<object>)(false);
                    }
                    await PostException(ex);
                }
            }, (y) => { return Connected(); });
            _SendTextCommand = new DelegateCommand(async (x) =>
            {
                try
                {
                    await SendCommand(SendText);
                }
                catch (Exception ex)
                {
                    await PostException(ex);
                }
            }, (y) => { return Connected(); });
            _ReadCommand = new DelegateCommand(async (x) =>
            {
                try
                {
                    if (x != null)
                    {
                        string command = (x as dynamic).command;
                        System.Diagnostics.Debug.WriteLine("ReadCommand:"+command);
                        returnAction = (x as dynamic).action as Action<object>;
                        if ((x as dynamic).timeout != null)
                        {
                            double timeout = (x as dynamic).timeout;
                            System.Diagnostics.Debug.WriteLine("Timeout: " + timeout);
                            timeoutTimer.Stop();
                            timeoutTimer.Interval = TimeSpan.FromSeconds(timeout);
                            RemoveHandlers();
                            timeoutTimer.Tick += TimeoutTimer_Tick;
                            timeoutTimer.Start();
                        }
                        await SendCommand(command);
                    }
                }
                catch (Exception ex)
                {
                    if (x != null)
                    {
                        (x as dynamic).action(null);
                    }
                    await PostException(ex);
                }
            }, (y) => { return Connected(); });
            _GetIP = new DelegateCommand((x) =>
            {
                ReadCommand.Execute((object)(new
                {
                    action = (Action<object>)((value) =>
                    {
                        System.Diagnostics.Debug.WriteLine("ReadCommand Action:" + value);
                    }),
                    command = "= wifi.sta.getip()",
                    timeout = 5.0,
                }));
            }, (y) => { return Connected(); });
            _ClearWifi = new DelegateCommand(async (x) =>
            {
                await SendCommand("wifi.setmode(wifi.STATION) wifi.sta.config(\"\",\"\") file.remove(\".wifi\")");
            }, (y) => { return Connected(); });
            _GetFiles = new DelegateCommand(async (x) =>
            {
                await SendCommand("for k,v in pairs(file.list()) do l = string.format(\" % -15s\",k) print(l..\"   \"..v..\" bytes\") end");
            }, (y) => { return Connected(); });
            _Restart = new DelegateCommand(async (x) =>
            {
                await SendCommand("node.restart()");
            }, (y) => { return Connected(); });
            _StopTimers = new DelegateCommand(async (x) =>
            {
                await SendCommand("tmr.stop(0)");
                await SendCommand("tmr.stop(1)");
            }, (y) => { return Connected(); });
        }

        private async Task PostException(Exception ex)
        {
            await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                this.Status = "Exception " + ex.Message;
            });
        }

        private async Task ConnectToPort()
        {
            string aqs = SerialDevice.GetDeviceSelector();
            var dis = await DeviceInformation.FindAllAsync(aqs);
            var deviceInfo = dis.Single(p => p.Name.Contains(port)); 
            serialPort = await SerialDevice.FromIdAsync(deviceInfo.Id);
            /* Configure serial settings */
            serialPort.WriteTimeout = TimeSpan.FromMilliseconds(1000);
            serialPort.ReadTimeout = TimeSpan.FromMilliseconds(1000);
            serialPort.BaudRate = 115200;
            serialPort.Parity = SerialParity.None;
            serialPort.StopBits = SerialStopBitCount.One;
            serialPort.DataBits = 8;
            ReadCancellationTokenSource = new CancellationTokenSource();
            Listen();
        }

        private void TimeoutTimer_SendTick(object sender, object e)
        {
            timeoutTimer.Stop();
            System.Diagnostics.Debug.WriteLine("TimeoutTimer_SendTick");
            RemoveHandlers();
            var action = sendAction;
            ClearActions();
            action?.Invoke(true);
            if (disconnectAfterAction)
            {
                disconnectAfterAction = false;
                CancelReadTask();
                CloseDevice();
            }
        }

        private void TimeoutTimer_Tick(object sender, object e)
        {
            timeoutTimer.Stop();
            var action = returnAction == null ? triggerAction : returnAction;
            System.Diagnostics.Debug.WriteLine("TimeoutTimer_Tick");
            RemoveHandlers();
            ClearActions();
            action?.Invoke(null);
            if (disconnectAfterAction)
            {
                disconnectAfterAction = false;
                CancelReadTask();
                CloseDevice();
            }
        }

        private void ClearActions()
        {
            triggerAction = null;
            sendAction = null;
            returnAction = null;
        }

        public void Reset()
        {
            timeoutTimer.Stop();
            RemoveHandlers();
            trigger = string.Empty;
            triggerAction = null;
            returnAction = null;
        }

        private void RemoveHandlers()
        {
            System.Diagnostics.Debug.WriteLine("RemoveHandlers");
            timeoutTimer.Tick -= TimeoutTimer_Tick;
            timeoutTimer.Tick -= TimeoutTimer_SendTick;
        }

        public bool Connected()
        {
            return serialPort != null;
        }

        private void RaisePropertyChangedEventHandlers()
        {
            RaisePropertyChanged("Connect");
            RaisePropertyChanged("Disconnect");
            RaisePropertyChanged("SendTextCommand");
            RaisePropertyChanged("ClearWifi");
            RaisePropertyChanged("GetIP");
            RaisePropertyChanged("Restart");
            RaisePropertyChanged("GetFiles");
            RaisePropertyChanged("ReadCommand");
        }

        public CoreDispatcher Dispatcher { get; set; }

        string _Status;
        public string Status
        {
            get
            {
                return _Status;
            }
            set
            {
                _Status = value;
                RaisePropertyChanged("Status");
            }
        }

        string _receivedText = string.Empty;
        public string ReceivedText
        {
            get
            {
                return _receivedText;
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _receivedText = string.Empty;
                }
                else
                {
                    string receivedText = _receivedText + Environment.NewLine + value;
                    string[] rt = receivedText.Split(split, StringSplitOptions.None);
                    _receivedText = String.Join(Environment.NewLine, rt.Reverse().Take(_lineCount).Reverse());
                }
                RaisePropertyChanged("ReceivedText");
            }
        }

        string _sendText;
        public string SendText
        {
            get
            {
                return _sendText;
            }
            set
            {
                _sendText = value;
                RaisePropertyChanged("SendText");
            }
        }

        int _lineCount = 20;
        public int LineCount
        {
            get
            {
                return _lineCount;
            }
            set
            {
                _lineCount = value;
                RaisePropertyChanged("LineCount");
            }
        }

        private async void Listen()
        {
            try
            {
                if (serialPort != null)
                {
                    dataReaderObject = new DataReader(serialPort.InputStream);

                    // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
                    dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

                    // keep reading the serial input
                    while (true)
                    {
                        await ReadAsync(ReadCancellationTokenSource.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.GetType().Name == "TaskCanceledException")
                {
                    CloseDevice();
                }
            }
            finally
            {
                // Cleanup once complete
                if (dataReaderObject != null)
                {
                    dataReaderObject.DetachStream();
                    dataReaderObject = null;
                }
            }
        }

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            // If task cancellation was requested, comply
            cancellationToken.ThrowIfCancellationRequested();

            // Create a task object to wait for data on the serialPort.InputStream
            Task<UInt32> loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLength).AsTask(cancellationToken);

            // Launch the task and wait
            UInt32 bytesRead = await loadAsyncTask;
            if (bytesRead > 0)
            {
                string text = dataReaderObject.ReadString(bytesRead);
                System.Diagnostics.Debug.WriteLine("R:"+text);
                if (!string.IsNullOrEmpty(trigger) && !string.IsNullOrEmpty(text))
                {
                    if (text.Contains(trigger))
                    {
                        timeoutTimer.Stop();
                        RemoveHandlers();
                        returnAction = triggerAction;
                        triggerAction = null;
                        trigger = string.Empty;
                    }
                }
                ReceivedText = text;
                if (returnAction != null)
                {
                    timeoutTimer.Stop();
                    RemoveHandlers();
                    triggerAction = null;
                    trigger = string.Empty;
                    var action = returnAction;
                    returnAction = null;
                    action?.Invoke(text);
                    if (disconnectAfterAction)
                    {
                        disconnectAfterAction = false;
                        CancelReadTask();
                        CloseDevice();
                    }
                }
            }
        }

        private void CancelReadTask()
        {
            if (ReadCancellationTokenSource != null)
            {
                if (!ReadCancellationTokenSource.IsCancellationRequested)
                {
                    ReadCancellationTokenSource.Cancel();
                }
            }
        }

        private void CloseDevice()
        {
            if (serialPort != null)
            {
                serialPort.Dispose();
            }
            serialPort = null;
        }

        private async Task SendCommand(string command)
        {
            using (DataWriter dataWriteObject = new DataWriter(serialPort.OutputStream))
            {
                try
                {
                    Status = "SendCommand:" + command;
                    System.Diagnostics.Debug.WriteLine(Status);
                    if (command.Length != 0)
                    {
                        // Load the text from the sendText input text box to the dataWriter object
                        dataWriteObject.WriteString(command + Environment.NewLine);
                        System.Diagnostics.Debug.WriteLine("SendCommand WriteString:" + command);

                        // Launch an async task to complete the write operation
                        uint bytesWritten = await dataWriteObject.StoreAsync();
                        System.Diagnostics.Debug.WriteLine("SendCommand StoreAsync:" + command);

                        System.Diagnostics.Debug.WriteLine("SendCommand storeAsyncTask:" + bytesWritten);
                        if (bytesWritten > 0)
                        {
                            System.Diagnostics.Debug.WriteLine("W:" + command);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Status = ex.Message;
                    System.Diagnostics.Debug.WriteLine("SendCommand catch:" + ex.Message);
                }
                finally
                {
                    // Cleanup once complete
                    if (dataWriteObject != null)
                    {
                        dataWriteObject.DetachStream();
                        System.Diagnostics.Debug.WriteLine("SendCommand finally");
                    }
                }
            }
        }

        DelegateCommand _Connect;
        public DelegateCommand Connect
        {
            get
            {
                return _Connect;
            }
        }

        DelegateCommand _Disconnect;
        public DelegateCommand Disconnect
        {
            get
            {
                return _Disconnect;
            }
        }

        DelegateCommand _GetIP;
        public DelegateCommand GetIP
        {
            get
            {
                return _GetIP;
            }
        }

        DelegateCommand _ClearWifi;
        public DelegateCommand ClearWifi
        {
            get
            {
                return _ClearWifi;
            }
        }

        DelegateCommand _GetFiles;
        public DelegateCommand GetFiles
        {
            get
            {
                return _GetFiles;
            }
        }

        DelegateCommand _Restart;
        public DelegateCommand Restart
        {
            get
            {
                return _Restart;
            }
        }

        DelegateCommand _StopTimers;
        public DelegateCommand StopTimers
        {
            get
            {
                return _StopTimers;
            }
        }

        DelegateCommand _SendTextCommand;
        public DelegateCommand SendTextCommand
        {
            get
            {
                return _SendTextCommand;
            }
        }

        DelegateCommand _ReadCommand;
        public DelegateCommand ReadCommand
        {
            get
            {
                return _ReadCommand;
            }
        }
    }
}
