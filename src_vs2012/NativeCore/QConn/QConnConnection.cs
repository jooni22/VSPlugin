﻿using System;
using System.Text;
using BlackBerry.NativeCore.Diagnostics;
using BlackBerry.NativeCore.QConn.Model;

namespace BlackBerry.NativeCore.QConn
{
    /// <summary>
    /// Class responsible for keeping the connection and sending/receiving messages to/from QConn service running on target.
    /// </summary>
    public sealed class QConnConnection : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private QDataSource _source;
        private readonly Endianess _endian;
        private readonly string _serviceName;

        /// <summary>
        /// Init constructor.
        /// </summary>
        public QConnConnection(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");

            _host = host;
            _port = port;
            _source = new QDataSource();
            _endian = Endianess.Unknown;

            Open();
        }

        /// <summary>
        /// Init constructor.
        /// </summary>
        public QConnConnection(string host, int port, string serviceName, Endianess endian)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");

            _host = host;
            _port = port;
            _source = new QDataSource();
            _endian = endian;
            _serviceName = serviceName;
        }

        ~QConnConnection()
        {
            Dispose(false);
        }

        #region Properties

        public bool IsConnected
        {
            get { return _source != null && _source.IsConnected; }
        }

        #endregion

        /// <summary>
        /// Opens the connection to target.
        /// </summary>
        public void Open()
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnConnection");

            // no need to do anything, if already connected
            if (IsConnected)
            {
                return;
            }

            var status = _source.Connect(_host, _port);
            if (status != HResult.OK)
                throw new QConnException("Unable to connect to QConn service (" + status + ")");

            // receive presentation info:
            int length;
            var response = Receive(out length);
            if (string.IsNullOrEmpty(response) || string.CompareOrdinal(response, "QCONN") != 0)
                throw new QConnException("Invalid service running on target");

            // PH: HACK: strange, but looks like
            // the initial invitation *sometimes* is split into two responses,
            // where the second one is only 3-byte control instruction, that translates
            // to an empty string, what we can simply ignore...
            if (length < 10)
            {
                response = Receive(out length);
            }

            if (!string.IsNullOrEmpty(_serviceName))
            {
                response = Send("service " + _serviceName);
            }
        }

        /// <summary>
        /// Closes connection to target.
        /// </summary>
        public void Close()
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnConnection");

            var status = _source.Close();
            if (status != HResult.OK)
                throw new QConnException("Unable to close connection to QConn service (" + status + ")");
        }

        #region Send-Receive

        /// <summary>
        /// Sends a command that expects a string response.
        /// It also does some response validation.
        /// </summary>
        public string Send(string command)
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnConnection");
            if (string.IsNullOrEmpty(command))
                throw new ArgumentNullException("command");

            Open();

            // send request:
            var status = _source.Send(Encoding.UTF8.GetBytes(command + "\r\n"));
            if (status != HResult.OK)
            {
                throw new QConnException("Unable to send command \"" + command + "\"");
            }

            // receive response:
            int length;
            var response = Receive(out length);

            // verify response:
            if (string.IsNullOrEmpty(response))
            {
                throw new QConnException("Unknown response for command \"" + command + "\"");
            }

            if (response.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
            {
                throw new QConnException("Command \"" + command + "\" finished with error: " + response.Substring(6));
            }

            return response;
        }

        /// <summary>
        /// Sends a command that will parse raw response.
        /// </summary>
        public IDataReader Request(string command)
        {
            if (_source == null)
                throw new ObjectDisposedException("QConnConnection");
            if (string.IsNullOrEmpty(command))
                throw new ArgumentNullException("command");

            Open();

            // send request:
            var status = _source.Send(Encoding.UTF8.GetBytes(command + "\r\n"));
            if (status != HResult.OK)
            {
                throw new QConnException("Unable to send command \"" + command + "\"");
            }

            // get response as proper stream-reader object with given endianess support:
            if (_endian == Endianess.LittleEndian)
                return new DataReaderLittleEndian(_source);
            return new DataReaderBigEndian(_source);
        }

        private string Receive(out int length)
        {
            // read all data:
            byte[] data;

            do
            {
                var status = _source.Receive(int.MaxValue, out data);
                length = data.Length;

                QTraceLog.WriteLine("Received response: {0} ({1})", data.Length, status);

                if (status != HResult.OK)
                    return null;
            } while (length == 2 && data[0] == '\r' && data[1] == '\n');

            return ResponseToString(data);
        }

        /// <summary>
        /// Converts raw response to string object, removing all controls chars.
        /// </summary>
        private static string ResponseToString(byte[] data)
        {
            const byte IAC = 255;
            const byte DONT = 254;
            const byte DO = 253;
            const byte WONT = 252;
            const byte WILL = 251;
            const byte SB = 250;
            const byte SE = 240;

            var buff = new StringBuilder();

            int state = 0;
            for (int i = 0; i < data.Length && (state != 0 || data[i] != 10); i++)
            {
                byte c = data[i];

                switch (state)
                {
                    case 0:
                        if (c == IAC)
                        {
                            state = 1;
                        }
                        else if (!char.IsControl((char)c))
                        {
                            buff.Append((char)c);
                        }
                        break;
                    case 1:
                        switch (c)
                        {
                            case IAC:
                                buff.Append((char)c);
                                state = 0;
                                break;
                            case WILL:
                            case WONT:
                            case DO:
                            case DONT:
                                state = 2;
                                break;
                            case SB:
                                state = 3;
                                break;
                            default:
                                state = 0;
                                break;
                        }
                        break;
                    case 2:
                        state = 0;
                        break;
                    case 3:
                        if (c == IAC)
                        {
                            state = 4;
                        }
                        break;
                    case 4:
                        if (c == SE)
                        {
                            state = 0;
                        }
                        else
                        {
                            state = 3;
                        }
                        break;
                }
            }

            return buff.ToString();
        }

        #endregion

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
                if (_source != null)
                {
                    _source.Dispose();
                    _source = null;
                }
            }
        }

        #endregion
    }
}
