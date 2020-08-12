using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using WebSocketSharp;

namespace OKExSDK
{
    /// <summary>
    /// mf个人重写 websocket接口
    /// </summary>
    public class WebSocket_OKEx
    {
        /// <summary>
        /// 日志接口
        /// </summary>
        private ILogger _logger;
        /// <summary>
        /// websocket地址
        /// </summary>
        private string _host;
        /// <summary>
        /// 判断超时定时器
        /// </summary>
        private Timer _timer;
        /// <summary>
        /// websocket对象
        /// </summary>
        protected WebSocket _WebSocket;
        /// <summary>
        /// 最后反馈时间
        /// </summary>
        private DateTime _lastReceivedTime;
        /// <summary>
        /// 是否自动重连
        /// </summary>
        public bool _autoConnect;
        /// <summary>
        /// idcm加密标记
        /// </summary>
        public readonly string Sign;
        /// <summary>
        /// 重新连接等待时长
        /// </summary>
        private const int RECONNECT_WAIT_SECOND = 60;
        /// <summary>
        /// 第二次重新连接等待时长
        /// </summary>
        private const int RENEW_WAIT_SECOND = 120;
        /// <summary>
        /// 定时器间隔时长秒
        /// </summary>
        private const int TIMER_INTERVAL_SECOND = 5;
        /// <summary>
        /// 接收消息回调事件
        /// </summary>
        public event Action<string> _eventMessage;
        /// <summary>
        /// websocket连接成功回调事件
        /// </summary>
        public event Action<EventArgs> _eventOpen;

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="host">地址</param>
        /// <param name="logger">日志接口</param>
        public WebSocket_OKEx(string host = null, ILogger logger = null)
        {
            this._host = host ?? "wss://real.okex.com:8443/ws/v3";
            this._logger = logger ?? NullLogger.Instance;
            this._timer = new Timer(TIMER_INTERVAL_SECOND * 1000);
            this._timer.Elapsed += _timer_Elapsed;
            InitializeWebSocket();
        }

        /// <summary>
        /// 超时定时器
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            double elapsedSecond = (DateTime.UtcNow - _lastReceivedTime).TotalSeconds;
            if (elapsedSecond > RECONNECT_WAIT_SECOND && elapsedSecond <= RENEW_WAIT_SECOND)
            {
                _logger.LogInformation("OKEx WebSocket reconnecting...");
                _WebSocket.Close();
                _WebSocket.Connect();
            }
            else if (elapsedSecond > RENEW_WAIT_SECOND)
            {
                _logger.LogInformation("OKEx WebSocket re-initialize...");
                Disconnect();
                UninitializeWebSocket();
                InitializeWebSocket();
                Connect();
            }
        }

        /// <summary>
        /// 初始化websocket
        /// </summary>
        private void InitializeWebSocket()
        {
            _WebSocket = new WebSocket(_host);
            //_WebSocket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.None;
            _WebSocket.OnError += _WebSocket_OnError;
            _WebSocket.OnOpen += _WebSocket_OnOpen;
            //_WebSocket.OnClose += _WebSocket_OnClose;
            _lastReceivedTime = DateTime.UtcNow;
            System.Threading.Thread thread = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(1000 * 10);
                    Send("ping");
                }
            });
            thread.IsBackground = true;
            thread.Priority = System.Threading.ThreadPriority.Lowest;
            thread.Start();
        }

        /// <summary>
        /// 取消websocket连接
        /// </summary>
        private void UninitializeWebSocket()
        {
            _WebSocket.OnOpen -= _WebSocket_OnOpen;
            _WebSocket.OnError -= _WebSocket_OnError;
            //_WebSocket.OnClose -= _WebSocket_OnClose;
            _WebSocket = null;
        }

        /// <summary>
        /// 连接到Websocket服务器
        /// </summary>
        /// <param name="autoConnect">断开连接后是否自动连接到服务器</param>
        public void Connect(bool autoConnect = true)
        {
            _WebSocket.OnMessage += _WebSocket_OnMessage;
            try
            {
                _WebSocket.Connect();
            }
            catch (System.Exception ex)
            {
                this._logger.LogError(ex, "OKEx连接不上");
            }
            _autoConnect = autoConnect;
            if (_autoConnect)
            {
                _timer.Enabled = true;
            }
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="apiKey"></param>
        /// <param name="secret"></param>
        /// <param name="phrase"></param>
        public void Login(string apiKey, string secret, string phrase)
        {
            var sign = Encryptor.MakeSign(apiKey, secret, phrase);
            Send(sign);
        }

        /// <summary>
        /// 断开与Websocket服务器的连接
        /// </summary>
        public void Disconnect()
        {
            _timer.Enabled = false;
            _WebSocket.OnMessage -= _WebSocket_OnMessage;
            _WebSocket.Close(CloseStatusCode.Normal);
        }

        /// <summary>
        /// websocket连接成功回调函数
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _WebSocket_OnOpen(object sender, EventArgs e)
        {
            _logger.LogDebug("OKEx WebSocket opened");
            _lastReceivedTime = DateTime.UtcNow;
            if (this._eventOpen != null)
            {
                this._eventOpen(e);
            }
        }

        /// <summary>
        /// websocket close
        /// </summary>
        /// <param name="e"></param>
        private void _WebSocket_OnClose(object e)
        {
            _logger.LogDebug("OKEx WebSocket closed");
        }

        /// <summary>
        /// websocket 接收到消息回调函数
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _WebSocket_OnMessage(object sender, MessageEventArgs e)
        {
            _lastReceivedTime = DateTime.UtcNow;
            string data = e.Data;
            if (e.IsBinary)
            {
                data = Decompress(e.RawData);
                if (data == "pong")
                {
                    return;
                }
            }
            if (this._eventMessage != null)
            {
                this._eventMessage(data);
            }
        }

        /// <summary>
        /// websocket 出错回调函数
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _WebSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            _logger.LogError(e.Exception, $"OKEx WebSocket error: {e.Message}");
        }

        /// <summary>
        /// 发送字符串，注：发流(byte[])时不会有响应
        /// </summary>
        /// <param name="b"></param>
        public void Send(string b)
        {
            try
            {
                if (_WebSocket.ReadyState == WebSocketState.Open)
                {
                    _WebSocket.Send(b);
                }
            }
            catch (System.Exception ex)
            {
                this._logger.LogError(ex, "OKEx websocket发送消息出错");
            }
        }

        /// <summary>
        /// 解压gzip
        /// </summary>
        /// <param name="baseBytes"></param>
        /// <returns></returns>
        private string Decompress(byte[] baseBytes)
        {
            try
            {
                using (var decompressedStream = new MemoryStream())
                using (var compressedStream = new MemoryStream(baseBytes))
                using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress, true))
                {
                    deflateStream.CopyTo(decompressedStream);
                    decompressedStream.Position = 0;
                    using (var streamReader = new StreamReader(decompressedStream, Encoding.UTF8))
                    {
                        return streamReader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Decompress");
            }
            return "";
        }

    }
}