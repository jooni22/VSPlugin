﻿using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using BlackBerry.NativeCore.Diagnostics;
using BlackBerry.NativeCore.Helpers;
using BlackBerry.NativeCore.QConn.Requests;
using BlackBerry.NativeCore.QConn.Response;
using BlackBerry.NativeCore.Tools;

namespace BlackBerry.NativeCore.QConn
{
    /// <summary>
    /// Manager class that connects securely with a target device and allows several unsecured connections.
    /// It requires a public SSH key (4kB) and a device password.
    /// </summary>
    public sealed class QConnDoor : IDisposable
    {
        /// <summary>
        /// Default port the service is operating on the device.
        /// </summary>
        public const int DefaultPort = 4455;
        /// <summary>
        /// Default interval for sending keep-alive command.
        /// </summary>
        public const int DefaultKeepAliveInterval = 5000;
        private const int DefaultResponseSize = 255;

        private QDataSource _source;
        private readonly object _lock;
        private Timer _keepAliveTimer;
        private bool _isAuthenticated;
        private bool _didNotified;

        /// <summary>
        /// Event raised each time status of an encrypted-channel to target is changed.
        /// </summary>
        public event EventHandler<QConnAuthenticationEventArgs> Authenticated;

        /// <summary>
        /// Default constructor.
        /// </summary>
        public QConnDoor()
        {
            _source = new QDataSource();
            _lock = new object();
        }

        ~QConnDoor()
        {
            Dispose(false);
        }

        #region Properties

        /// <summary>
        /// Gets an indication, if connection to target has been established.
        /// </summary>
        public bool IsConnected
        {
            get { return _source != null && _source.IsConnected; }
        }

        /// <summary>
        /// Gets an indication, if secured communication channel has been established to the device and SSH keys are transmitted.
        /// </summary>
        public bool IsAuthenticated
        {
            get { return IsConnected && _isAuthenticated; }
        }

        /// <summary>
        /// Gets or sets the event dispatcher responsible for thread managment of raised events.
        /// </summary>
        public IEventDispatcher EventDispatcher
        {
            get;
            set;
        }

        #endregion

        /// <summary>
        /// Closes connection with current target.
        /// </summary>
        public void Close()
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            KeepAlive(0); // stop keep-alive timer

            if (!_source.IsConnected)
            {
                NotifyAuthenticationChanged(false);
                return;
            }

            // request a close of the connection on target:
            Send(new SecureTargetCloseRequest());
            // ignore the result of CLOSE-request...

            // close connection:
            var result = _source.Close();
            QTraceLog.WriteLine("Disconnected from target {0}", _source);

            NotifyAuthenticationChanged(false);
            if (result != HResult.OK)
                throw new SecureTargetConnectionException(HResult.Fail, "Unable to close connection with target");

            _didNotified = false;
        }

        /// <summary>
        /// Authenticates on a target.
        /// </summary>
        public void Open(string host, int port, string password, string sshPublicKeyFileName)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException("password");
            if (string.IsNullOrEmpty(sshPublicKeyFileName))
                throw new ArgumentNullException("sshPublicKeyFileName");

            Open(host, port, password, File.ReadAllBytes(sshPublicKeyFileName));
        }

        /// <summary>
        /// Authenticates on a target.
        /// </summary>
        public void Open(string host, string password, string sshPublicKeyFileName)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException("password");
            if (string.IsNullOrEmpty(sshPublicKeyFileName))
                throw new ArgumentNullException("sshPublicKeyFileName");

            Open(host, DefaultPort, password, File.ReadAllBytes(sshPublicKeyFileName));
        }

        /// <summary>
        /// Authenticates on a target.
        /// </summary>
        public void Open(string host, int port, string password, byte[] sshKey)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException("password");
            if (sshKey == null || sshKey.Length == 0)
                throw new ArgumentNullException("sshKey");

            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            // is already connected?
            if (IsAuthenticated)
                return;

            var result = _source.Connect(host, port, 0);
            if (result != HResult.OK)
            {
                QTraceLog.WriteLine("Failed to connect to target");
                throw new SecureTargetConnectionException(result, string.Concat("Unable to connect to target ", host, ":", port));
            }

            // try to initialize communication:
            Execute(new SecureTargetHelloRequest());

            RSAParameters publicKey;
            RSAParameters privateKey;

            using (var rsa = new RSACryptoServiceProvider(1024))
            {
                try
                {
                    // don't store any keys in persistent storages of current Windows account:
                    rsa.PersistKeyInCsp = false;

                    publicKey = rsa.ExportParameters(false);
                    privateKey = rsa.ExportParameters(true);
                    // more info about parameters and their meaning is here:
                    // http://msdn.microsoft.com/en-us/library/system.security.cryptography.rsaparameters%28v=vs.90%29.aspx
                }
                catch (Exception ex)
                {
                    QTraceLog.WriteException(ex, "Unable to generate encryption keys");
                    throw new SecureTargetConnectionException(HResult.Fail, "Unable to generate encryption key");
                }
            }

            // initialize encryption negotiation:
            var response = Execute(new SecureTargetChallengeRequest(publicKey.Modulus));

            var encryptedChallenge = response as SecureTargetEncryptedChallengeResponse;
            if (encryptedChallenge == null)
            {
                QTraceLog.WriteLine("Unexpected response for encryption challenge");
                throw new SecureTargetConnectionException(HResult.Fail, "Unexpected response for encryption challenge");
            }

            if (encryptedChallenge.Challenge.ExpectedSignatureType != 1)
            {
                throw new Exception("Invalid signature type in encryption challenge: 0x" + encryptedChallenge.Challenge.ExpectedSignatureType.ToString("X4"));
            }

            // decrypt the message:
            var decryptedChallenge = encryptedChallenge.Challenge.Decrypt(publicKey, privateKey);
            if (decryptedChallenge == null)
            {
                throw new SecureTargetConnectionException(HResult.Fail, "Unable to decipher encryption challenge data");
            }

            if (encryptedChallenge.Challenge.ExpectedSignatureLength != decryptedChallenge.Signature.Length)
            {
                throw new Exception("Invalid signature length in encryption challenge: " + encryptedChallenge.Challenge.ExpectedSignatureLength);
            }

            // confirm encrypted channel:
            response = Execute(new SecureTargetDecryptedChallengeRequest(decryptedChallenge.DecryptedBlob, decryptedChallenge.Signature, decryptedChallenge.SessionKey));

            if (!(response is SecureTargetFeedbackNoPasswordRequired))
            {
                // prepare for sending password and ssh-public-key:
                response = Execute(new SecureTargetAuthenticateChallengeRequest());

                var authenticateResponse = response as SecureTargetAuthenticateChallengeResponse;
                if (authenticateResponse == null)
                {
                    throw new SecureTargetConnectionException(HResult.InvalidFrameCode, "Authentication negotiation failed");
                }

                // send password-hash:
                Execute(new SecureTargetAuthenticateRequest(password, authenticateResponse.Algorithm, authenticateResponse.Iterations, authenticateResponse.Salt, authenticateResponse.Challenge,
                    decryptedChallenge.SessionKey));
                QTraceLog.WriteLine("Successfully authenticated with target credentials.");
            }

            // send ssh-key:
            QTraceLog.WriteLine("Sending ssh key to target");
            Execute(new SecureTargetSendSshPublicKeyRequest(sshKey, decryptedChallenge.SessionKey));

            // and start all services on target:
            Execute(new SecureTargetStartServicesRequest());

            QTraceLog.WriteLine("Successfully connected. This application must remain running in order to use debug tools. Exiting the application will terminate this connection.");
            NotifyAuthenticationChanged(true);
        }

        /// <summary>
        /// Asynchronously tries to authenticate on a target.
        /// </summary>
        public void OpenAsync(string host, string password, string sshPublicKeyFileName)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException("password");
            if (string.IsNullOrEmpty(sshPublicKeyFileName))
                throw new ArgumentNullException("sshPublicKeyFileName");

            OpenAsync(host, DefaultPort, password, File.ReadAllBytes(sshPublicKeyFileName));
        }

        /// <summary>
        /// Asynchronously tries to authenticate on a target.
        /// </summary>
        public void OpenAsync(string host, int port, string password, byte[] sshKey)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentNullException("password");
            if (sshKey == null || sshKey.Length == 0)
                throw new ArgumentNullException("sshKey");

            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            // is already connected?
            if (IsAuthenticated)
                return;

            var action = new Action<string, int, string, byte[]>(Open);
            action.BeginInvoke(host, port, password, sshKey, OpenAsyncCompleted, action);
        }

        private void OpenAsyncCompleted(IAsyncResult ar)
        {
            var action = (Action<string, int, string, byte[]>)ar.AsyncState;

            try
            {
                action.EndInvoke(ar);
            }
            catch
            {
                NotifyAuthenticationChanged(false);
            }
        }

        /// <summary>
        /// Sends single keep-alive request to the target.
        /// Without it the secured and unsecured connections will be automatically closed by the target.
        /// Suggestion is to transmit this request each 5sec.
        /// See overloaded method, where you can specify the interval to have it sent automatically.
        /// </summary>
        public void KeepAlive()
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");
            if (!IsAuthenticated)
                throw new SecureTargetConnectionException(HResult.Fail, "Not authenticated to target, call Connect() first");

            var response = InternalSendKeepAlive();
            VerifyResponse(response);

            QTraceLog.WriteLine("Keep-alive confirmed from {0} - [OK]", _source);
        }

        /// <summary>
        /// Starts a timer issuing a keep-alive request to the target on given internals.
        /// It will stop only, if connection is closed or when this method is invoked with zero.
        /// </summary>
        /// <param name="millisecInterval">Number of milliseconds the keep-alive request should be issued</param>
        public void KeepAlive(uint millisecInterval)
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            // stop the timer, if running:
            if (millisecInterval == 0)
            {
                lock (_lock)
                {
                    if (_keepAliveTimer != null)
                    {
                        _keepAliveTimer.Dispose();
                        _keepAliveTimer = null;
                    }
                }

                return;
            }

            if (!IsAuthenticated)
                throw new SecureTargetConnectionException(HResult.Fail, "Not authenticated to target, call Connect() first");

            // start or update the timer:
            lock (_lock)
            {
                if (_keepAliveTimer == null)
                    _keepAliveTimer = new Timer(OnAliveTick, this, millisecInterval, millisecInterval);
                else
                    _keepAliveTimer.Change(0, millisecInterval);
            }
        }

        private void OnAliveTick(object state)
        {
            var response = InternalSendKeepAlive();

            // did the delivery failed?
            if (response == null || response.Status != HResult.OK)
            {
                // stop the timer, as no more needed:
                lock (_lock)
                {
                    if (_keepAliveTimer != null)
                    {
                        _keepAliveTimer.Dispose();
                        _keepAliveTimer = null;
                    }
                }

                // and notify about failure:
                QTraceLog.WriteLine("Failed to deliver keep-alive request to target {0} ({1})", _source, response != null ? response.ToString() : "Connection already closed");
                NotifyAuthenticationChanged(false);
            }
            else
            {
                QTraceLog.WriteLine("Keep-alive confirmed from {0} - [OK]", _source);
            }
        }

        /// <summary>
        /// Sends keep-alive request to the target and returns its response.
        /// </summary>
        private SecureTargetResult InternalSendKeepAlive()
        {
            if (!IsAuthenticated)
                return null;

            QTraceLog.WriteLine("Sending keep-alive");

            try
            {
                Send(new SecureTargetKeepAliveRequest());
                return Receive();
            }
            catch (Exception ex)
            {
                QTraceLog.WriteException(ex, "Unable to send keep-alive request");
                return null;
            }
        }

        /// <summary>
        /// Sends specified requests, waits for response and verifies it.
        /// </summary>
        private SecureTargetResult Execute(SecureTargetRequest request)
        {
            Send(request);
            var response = Receive();

            VerifyResponse(response);
            return response;
        }

        private void Send(SecureTargetRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            _source.Send(request.GetData());
        }

        private SecureTargetResult Receive()
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnDoor");

            byte[] result;
            var status = _source.Receive(DefaultResponseSize, out result);

            if (result != null && result.Length > 0)
            {
                ushort packetLength = BitHelper.BigEndian_ToUInt16(result, 0);

                // PH: HINT: don't know why, but on PlayBook packageLength doesn't correctly
                // define the number of bytes received, that's why we compare it with buffer;
                // on Z10 and Z30 the packageLength equals returned result.Length
                if (packetLength > DefaultResponseSize)
                {
                    QTraceLog.WriteLine("Packet length larger than buffer, expected size: " + packetLength);
                    return new SecureTargetResult(result, HResult.BufferTooSmall);
                }
            }

            // interpret response:
            return SecureTargetResult.Load(result, status);
        }

        /// <summary>
        /// Verifies correctness of the response and throws appropriate exception if needed.
        /// </summary>
        private void VerifyResponse(SecureTargetResult result)
        {
            // received anything?
            if (result == null)
            {
                throw new SecureTargetConnectionException(HResult.Abort, "No response received in expected time");
            }

            // does the protocol version match?
            var versionMismatch = result as SecureTargetFeedbackMismatchedVersion;
            if (versionMismatch != null)
            {
                throw new SecureTargetConnectionException(result.Status, versionMismatch.Message);
            }

            // request was rejected?
            var rejected = result as SecureTargetFeedbackRejected;
            if (rejected != null)
            {
                throw new SecureTargetConnectionException(result.Status, "Connection refused: " + rejected.Reason);
            }

            // received correctly formatted response?
            if (result.Status != HResult.OK)
            {
                // unknown response?
                var response = result as SecureTargetResponse;
                if (response != null && result.Status == HResult.InvalidFrameCode)
                {
                    throw new SecureTargetConnectionException(result.Status, "The target returned an improper response code: " + response.Code);
                }

                throw new SecureTargetConnectionException(result.Status, "A network error occurred while communicating with the target.");
            }

            // all was OK... finally!
        }

        private void NotifyAuthenticationChanged(bool isAuthenticated)
        {
            if (isAuthenticated != _isAuthenticated || !_didNotified)
            {
                _didNotified = true;
                _isAuthenticated = isAuthenticated;

                // notify using dispatcher or just by calling handlers from the same thread:
                var dispatcher = EventDispatcher;
                if (dispatcher != null)
                {
                    dispatcher.Invoke(Authenticated, this, new QConnAuthenticationEventArgs(this, _isAuthenticated));
                }
                else
                {
                    var handler = Authenticated;
                    if (handler != null)
                    {
                        handler(this, new QConnAuthenticationEventArgs(this, _isAuthenticated));
                    }
                }
            }
        }

        #region IDisposable Implementation

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                // stop sending keep-alive:
                if (_keepAliveTimer != null)
                {
                    _keepAliveTimer.Dispose();
                    _keepAliveTimer = null;
                }

                // release data-source:
                if (_source != null)
                {
                    Close();
                    _source = null;
                }

                // clear handlers:
                Authenticated = null;
            }
        }

        #endregion
    }
}
