﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TlsClientStream
{
    internal class TlsClientStream : Stream
    {
        const int MaxEncryptedRecordLen = (1 << 14) /* data */ + 16 + 64 + 256 /* iv + mac + padding (accept long CBC mode padding) */;

        // Buffer data
        byte[] _buf = new byte[5 /* header */ + MaxEncryptedRecordLen];
        int _readStart;
        int _readEnd;
        int _packetLen;

        Stream _baseStream;

        // Connection states
        // Read connection state is for the purpose when we have sent ChangeCipherSpec but the server hasn't yet
        ConnectionState _connState;
        ConnectionState _readConnState;
        ConnectionState _pendingConnState;

        RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        // Info about the current message in the buffer
        ContentType _contentType;
        int _plaintextLen;
        int _plaintextStart;

        // Temp buffer to hold sequence number
        byte[] _tempBuf8 = new byte[8];
        // Temp buffer for GCM
        byte[] _temp512;

        // Holds buffered handshake messages that will be dequeued as soon as a proper final message has been received (ServerHelloDone or Finished)
        HandshakeMessagesBuffer _handshakeMessagesBuffer = new HandshakeMessagesBuffer();
        HandshakeData _handshakeData;

        // User parameters
        bool _noRenegotiationExtensionSupportIsFatal = false;
        string _hostName = null;
        X509CertificateCollection _clientCertificates;
        System.Net.Security.RemoteCertificateValidationCallback _remoteCertificationValidationCallback;

        bool _waitingForChangeCipherSpec;
        bool _waitingForFinished;

        // When renegotiating we use another buffer to avoid overwriting the normal one
        byte[] _renegotiationTempWriteBuf;

        // Used in Read and Write methods to keep track of position
        int _writePos;
        int _decryptedReadPos;
        int _decryptedReadEnd;

        // When we write in the middle of a handshake, we must block until the handshake is completed before
        // we can actually write the data, due to a bug in OpenSSL. If we at the same time receive data from
        // the server, we must buffer it so it can be delivered to the application later.
        // Note that this is quite uncommon, since one normally drains the read buffer before writing.
        const int MaxBufferedReadData = 10 * (1 << 20); // 10 MB
        Queue<byte[]> _bufferedReadData;
        int _posBufferedReadData;
        int _lenBufferedReadData;

        // Stream state
        bool _eof;
        bool _closed;

        /// <summary>
        /// Creates a new TlsClientStream with the given underlying stream.
        /// The handshake must be manually initiated with the method PerformInitialHandshake.
        /// </summary>
        /// <param name="baseStream">Base stream</param>
        public TlsClientStream(Stream baseStream)
        {
            _connState = new ConnectionState();
            _readConnState = _connState;
            _baseStream = baseStream;
        }

        #region Record layer

        /// <summary>
        /// Makes sure there is at least one full record available at _readStart.
        /// Also sets _packetLen (does not include packet header of 5 bytes).
        /// </summary>
        /// <returns>True on success, false on End Of Stream.</returns>
        bool ReadRecord()
        {
            int packetLength = -1;
            while (true)
            {
                if (packetLength == -1 && _readEnd - _readStart >= 5)
                {
                    // We have at least a header in our buffer, so extract the length
                    packetLength = (_buf[_readStart + 3] << 8) | _buf[_readStart + 4];
                    if (packetLength > MaxEncryptedRecordLen)
                    {
                        SendAlertFatal(AlertDescription.RecordOverflow);
                    }
                }
                if (packetLength != -1 && 5 + packetLength <= _readEnd - _readStart)
                {
                    // The whole record fits in the buffer. We are done.
                    _packetLen = packetLength;
                    return true;
                }
                if (_readEnd - _readStart > 0 && _readStart > 0)
                {
                    // We only have a partial record in the buffer,
                    // move that to the beginning to be able to read as much as possible from the network.
                    Buffer.BlockCopy(_buf, _readStart, _buf, 0, _readEnd - _readStart);
                    _readEnd -= _readStart;
                    _readStart = 0;
                }
                if (packetLength == -1 || _readEnd < 5 + packetLength)
                {
                    if (_readStart == _readEnd)
                    {
                        // The read buffer is empty, so start reading at the start of the buffer
                        _readStart = 0;
                        _readEnd = 0;
                    }
                    int read = _baseStream.Read(_buf, _readEnd, _buf.Length - _readEnd);
                    if (read == 0)
                    {
                        return false;
                    }
                    _readEnd += read;
                }
            }
        }

        // ReadRecord should be called first.
        // Sets _contentType, _plaintextStart and _plaintextLength, and increments _readStart
        void Decrypt()
        {
            _contentType = (ContentType)_buf[_readStart];
            if (_readConnState.CipherSuite == null)
            {
                _plaintextStart = _readStart + 5;
                _plaintextLen = _packetLen;
            }
            else if (_readConnState.CipherSuite.AesMode == AesMode.CBC)
            {
                var minPlaintextBytes = _readConnState.MacLen + 1;
                var minEncryptedBlocks = (minPlaintextBytes + _readConnState.BlockLen - 1) / _readConnState.BlockLen;
                var minEncryptedBytes = minEncryptedBlocks * _readConnState.BlockLen;
                if (_packetLen < _readConnState.IvBuf.Length + minEncryptedBytes || (_packetLen - _readConnState.IvBuf.Length) % _readConnState.BlockLen != 0)
                    SendAlertFatal(AlertDescription.BadRecordMac);
                Buffer.BlockCopy(_buf, _readStart + 5, _readConnState.IvBuf, 0, _readConnState.IvBuf.Length);

                _readConnState.ReadAes.IV = _readConnState.IvBuf;
                int cipherStartPos = _readStart + 5 + _readConnState.IvBuf.Length;
                int cipherLen = _packetLen - _readConnState.IvBuf.Length;

                using (var decryptor = _readConnState.ReadAes.CreateDecryptor())
                {
                    decryptor.TransformBlock(_buf, cipherStartPos, cipherLen, _buf, cipherStartPos);
                }
                int paddingLen = _buf[cipherStartPos + cipherLen - 1];
                bool paddingFail = false;
                if (paddingLen > cipherLen - 1 - _readConnState.MacLen)
                {
                    // We have found illegal padding. Instead of just send fatal alert directly,
                    // still do the mac computation and let it fail to deal with timing attacks.
                    paddingLen = 0;
                    paddingFail = true;
                }
                int plaintextLen = cipherLen - 1 - paddingLen - _readConnState.MacLen;

                // We don't need the IV anymore in the buffer, so overwrite it with seq_num + header to calculate MAC
                Buffer.BlockCopy(_buf, _readStart, _buf, cipherStartPos - 5, 3);
                Utils.WriteUInt16(_buf, cipherStartPos - 2, (ushort)plaintextLen);
                Utils.WriteUInt64(_buf, cipherStartPos - 5 - 8, _readConnState.ReadSeqNum);

                var hmac = _readConnState.ReadMac.ComputeHash(_buf, cipherStartPos - 5 - 8, 8 + 5 + plaintextLen);
                
                if (!Utils.ArraysEqual(hmac, 0, _buf, cipherStartPos + plaintextLen, hmac.Length))
                    SendAlertFatal(AlertDescription.BadRecordMac);

                // Verify that the padding bytes contain the correct value (paddingLen)
                for (int i = 0; i < paddingLen; i++)
                    if (_buf[cipherStartPos + cipherLen - 2 - i] != paddingLen)
                        SendAlertFatal(AlertDescription.BadRecordMac);

                // Very unlikely MAC didn't catch this
                if (paddingFail)
                    SendAlertFatal(AlertDescription.BadRecordMac);

                _plaintextStart = cipherStartPos;
                _plaintextLen = plaintextLen;
            }
            else if (_readConnState.CipherSuite.AesMode == AesMode.GCM)
            {
                Utils.WriteUInt32(_readConnState.IvBuf, 0, _readConnState.ReadSalt);
                Buffer.BlockCopy(_buf, _readStart + 5, _readConnState.IvBuf, 4, _readConnState.IvLen);
                var cipherStartPos = _readStart + 5 + _readConnState.IvLen;
                var plaintextLen = _packetLen - 16 - _readConnState.IvLen;
                if (plaintextLen < 0)
                    SendAlertFatal(AlertDescription.BadRecordMac);
                var ok = GaloisCounterMode.GCMAD(_readConnState.ReadAesECB, _readConnState.IvBuf, _buf, cipherStartPos, plaintextLen, _readConnState.ReadSeqNum, (byte)_contentType, _readConnState.ReadGCMTable, _temp512);
                if (!ok)
                    SendAlertFatal(AlertDescription.BadRecordMac);
                _plaintextStart = cipherStartPos;
                _plaintextLen = plaintextLen;
            }
            _readStart += 5 + _packetLen;
            _readConnState.ReadSeqNum++;
        }

        // startPos: at content type, len: plaintext length without header
        // updates seq num
        /// <summary>
        /// Encrypts a record.
        /// A header should be at startPos containing TLS record type and version.
        /// At startPos + 5 + ivLen the plaintext should start.
        /// </summary>
        /// <param name="startPos">Should point to the beginning of the record (content type)</param>
        /// <param name="len">Plaintext length (without header)</param>
        /// <returns>The byte position after the last byte in this encrypted record</returns>
        int Encrypt(int startPos, int len)
        {
            if (_connState.CipherSuite != null && _connState.CipherSuite.AesMode == AesMode.CBC)
            {
                // Update length first with plaintext length
                Utils.WriteUInt16(_buf, startPos + 3, (ushort)len);

                Utils.WriteUInt64(_tempBuf8, 0, _connState.WriteSeqNum++);
                
                _connState.WriteMac.Initialize();
                _connState.WriteMac.TransformBlock(_tempBuf8, 0, 8, _tempBuf8, 0);
                _connState.WriteMac.TransformBlock(_buf, startPos, 5, _buf, startPos);
                _connState.WriteMac.TransformBlock(_buf, startPos + 5 + _connState.IvBuf.Length, len, _buf, startPos + 5 + _connState.IvBuf.Length);
                _connState.WriteMac.TransformFinalBlock(_buf, 0, 0);
                var mac = _connState.WriteMac.Hash;
                
                Buffer.BlockCopy(mac, 0, _buf, startPos + 5 + _connState.IvBuf.Length + len, mac.Length);
                Utils.ClearArray(mac);

                var paddingLen = _connState.BlockLen - (len + _connState.MacLen + 1) % _connState.BlockLen;
                for (var i = 0; i < paddingLen + 1; i++)
                    _buf[startPos + 5 + _connState.IvBuf.Length + len + _connState.MacLen + i] = (byte)paddingLen;

                int encryptedLen = len + _connState.MacLen + paddingLen + 1;

                // Update length now with encrypted length
                Utils.WriteUInt16(_buf, startPos + 3, (ushort)(_connState.IvBuf.Length + encryptedLen));

                _rng.GetBytes(_connState.IvBuf);
                Buffer.BlockCopy(_connState.IvBuf, 0, _buf, startPos + 5, _connState.IvBuf.Length);
                _connState.WriteAes.IV = _connState.IvBuf;
                using (var encryptor = _connState.WriteAes.CreateEncryptor())
                {
                    encryptor.TransformBlock(_buf, startPos + 5 + _connState.IvBuf.Length, encryptedLen, _buf, startPos + 5 + _connState.IvBuf.Length);
                }
                return startPos + 5 + _connState.IvBuf.Length + encryptedLen;
            }
            else if (_connState.CipherSuite != null && _connState.CipherSuite.AesMode == AesMode.GCM)
            {
                Utils.WriteUInt32(_connState.IvBuf, 0, _connState.WriteSalt);
                Utils.WriteUInt64(_connState.IvBuf, 4, _connState.WriteSeqNum);
                Utils.WriteUInt64(_buf, startPos + 5, _connState.WriteSeqNum);
                GaloisCounterMode.GCMAE(_connState.WriteAesECB, _connState.IvBuf, _buf, startPos + 5 + _connState.IvLen, len, _connState.WriteSeqNum++, _buf[startPos], _connState.WriteGCMTable, _temp512);
                Utils.WriteUInt16(_buf, startPos + 3, (ushort)(_connState.IvLen + len + 16));
                return startPos + 5 + _connState.IvLen + len + 16;
            }
            else // Null cipher
            {
                // Update length
                Utils.WriteUInt16(_buf, startPos + 3, (ushort)len);
                return startPos + 5 + len;
            }
        }

        #endregion

        #region Handshake infrastructure

        void UpdateHandshakeHash(byte[] buf, int offset, int len)
        {
            // .NET hash api does not allow us to clone hash states ...
            if (_handshakeData.HandshakeHash1 != null)
                _handshakeData.HandshakeHash1.TransformBlock(buf, offset, len, buf, offset);
            if (_handshakeData.HandshakeHash1_384 != null)
                _handshakeData.HandshakeHash1_384.TransformBlock(buf, offset, len, buf, offset);
            if (_handshakeData.HandshakeHash2 != null)
                _handshakeData.HandshakeHash2.TransformBlock(buf, offset, len, buf, offset);
            if (_handshakeData.HandshakeHash2_384 != null)
                _handshakeData.HandshakeHash2_384.TransformBlock(buf, offset, len, buf, offset);
            if (_handshakeData.CertificateVerifyHash_SHA1 != null)
                _handshakeData.CertificateVerifyHash_SHA1.TransformBlock(buf, offset, len, buf, offset);
        }

        void GetInitialHandshakeMessages(bool allowApplicationData = false)
        {
            while (!_handshakeMessagesBuffer.HasServerHelloDone)
            {
                if (!ReadRecord())
                    throw new IOException("Connection EOF in initial handshake");
                Decrypt();

                switch (_contentType)
                {
                    case ContentType.Alert:
                        HandleAlertMessage();
                        break;
                    case ContentType.Handshake:
                        _handshakeMessagesBuffer.AddBytes(_buf, _plaintextStart, _plaintextLen, HandshakeMessagesBuffer.IgnoreHelloRequestsSettings.IgnoreHelloRequests);
                        if (_handshakeMessagesBuffer.Messages.Count > 5)
                        {
                            // There can never be more than 5 handshake messages in a handshake
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        }
                        break;
                    case ContentType.ApplicationData:
                        EnqueueReadData(allowApplicationData);
                        break;
                    default:
                        SendAlertFatal(AlertDescription.UnexpectedMessage);
                        break;
                }
            }

            var responseLen = TraverseHandshakeMessages();

            _baseStream.Write(_buf, 0, responseLen);
            _baseStream.Flush();
            _writePos = 5 + _pendingConnState.IvLen;
            _waitingForChangeCipherSpec = true;
        }

        int TraverseHandshakeMessages()
        {
            HandshakeType lastType = 0;
            int responseLen = 0;

            for (var i = 0; i < _handshakeMessagesBuffer.Messages.Count; i++)
            {
                int pos = 0;
                var buf = _handshakeMessagesBuffer.Messages[i];
                UpdateHandshakeHash(buf, 0, buf.Length);
                HandshakeType msgType = (HandshakeType)buf[pos++];
                int msgLen = Utils.ReadUInt24(buf, ref pos);
                
                switch (msgType)
                {
                    case HandshakeType.ServerHello:
                        if (lastType != 0)
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        ParseServerHelloMessage(buf, ref pos, pos + msgLen);
                        break;
                    case HandshakeType.Certificate:
                        if (lastType != HandshakeType.ServerHello)
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        ParseCertificateMessage(buf, ref pos);
                        break;
                    case HandshakeType.ServerKeyExchange:
                        if (lastType != HandshakeType.Certificate)
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        ParseServerKeyExchangeMessage(buf, ref pos);
                        break;
                    case HandshakeType.CertificateRequest:
                        if (lastType != HandshakeType.Certificate && lastType != HandshakeType.ServerKeyExchange)
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        ParseCertificateRequest(buf, ref pos);
                        break;
                    case HandshakeType.ServerHelloDone:
                        if (msgLen != 0)
                            SendAlertFatal(AlertDescription.DecodeError);
                        if ((lastType != HandshakeType.Certificate && lastType != HandshakeType.ServerKeyExchange && lastType != HandshakeType.CertificateRequest)
                            || i != _handshakeMessagesBuffer.Messages.Count - 1)
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        responseLen = GenerateHandshakeResponse();
                        break;
                    default:
                        SendAlertFatal(AlertDescription.UnexpectedMessage);
                        break;
                }
                if (pos != 4 + msgLen)
                    SendAlertFatal(AlertDescription.DecodeError);
                lastType = msgType;
            }
            _handshakeMessagesBuffer.ClearMessages();
            return responseLen;
        }

        // Here we send all client messages in response to server hello
        int GenerateHandshakeResponse()
        {
            int offset = 0;
            var ivLen = _connState.IvBuf == null ? 0 : _connState.IvLen;
            if (_handshakeData.CertificateTypes != null) // Certificate request has been sent by the server
            {
                SendHandshakeMessage(SendClientCertificate, ref offset, ivLen);
            }
            switch (_pendingConnState.CipherSuite.KeyExchange)
            {
                case KeyExchange.DHE_RSA:
                case KeyExchange.DHE_DSS:
                    SendHandshakeMessage(SendClientKeyExchangeDhe, ref offset, ivLen);
                    break;
                case KeyExchange.ECDHE_RSA:
                case KeyExchange.ECDHE_ECDSA:
                    SendHandshakeMessage(SendClientKeyExchangeEcdhe, ref offset, ivLen);
                    break;
                case KeyExchange.ECDH_ECDSA:
                case KeyExchange.ECDH_RSA:
                    SendHandshakeMessage(SendClientKeyExchangeEcdh, ref offset, ivLen);
                    break;
                case KeyExchange.RSA:
                    SendHandshakeMessage(SendClientKeyExchangeRsa, ref offset, ivLen);
                    break;
                default:
                    throw new InvalidOperationException();
            }
            if (_handshakeData.CertificateTypes != null && _handshakeData.SelectedClientCertificate != null)
            {
                SendHandshakeMessage(SendCertificateVerify, ref offset, ivLen);
            }

            var cipherSpecStart = offset;
            SendChangeCipherSpec(ref offset, ivLen);
            offset = Encrypt(cipherSpecStart, 1);

            // Key generation from Master Secret
            using (var hmac = _pendingConnState.CipherSuite.CreatePrfHMAC(_pendingConnState.MasterSecret))
            {
                var mode = _pendingConnState.CipherSuite.AesMode;
                var isCbc = mode == AesMode.CBC;
                var isGcm = mode == AesMode.GCM;

                var concRandom = new byte[_pendingConnState.ServerRandom.Length + _pendingConnState.ClientRandom.Length];
                Buffer.BlockCopy(_pendingConnState.ServerRandom, 0, concRandom, 0, _pendingConnState.ServerRandom.Length);
                Buffer.BlockCopy(_pendingConnState.ClientRandom, 0, concRandom, _pendingConnState.ServerRandom.Length, _pendingConnState.ClientRandom.Length);
                var macLen = isCbc ? _pendingConnState.CipherSuite.MACLen / 8 : 0;
                var aesKeyLen = _pendingConnState.CipherSuite.AesKeyLen / 8;
                var IVLen = isGcm ? 4 : 0;
                var keyBlock = Utils.PRF(hmac, "key expansion", concRandom, macLen * 2 + aesKeyLen * 2 + IVLen * 2);
                byte[] writeMac = new byte[macLen], readMac = new byte[macLen], writeKey = new byte[aesKeyLen], readKey = new byte[aesKeyLen];
                Buffer.BlockCopy(keyBlock, 0, writeMac, 0, macLen);
                Buffer.BlockCopy(keyBlock, macLen, readMac, 0, macLen);
                Buffer.BlockCopy(keyBlock, macLen * 2, writeKey, 0, aesKeyLen);
                Buffer.BlockCopy(keyBlock, macLen * 2 + aesKeyLen, readKey, 0, aesKeyLen);
                if (isCbc)
                {
                    _pendingConnState.WriteMac = _pendingConnState.CipherSuite.CreateHMAC(writeMac);
                    _pendingConnState.ReadMac = _pendingConnState.CipherSuite.CreateHMAC(readMac);
                }
                _pendingConnState.WriteAes = new AesCryptoServiceProvider() { Key = writeKey, Mode = isCbc ? CipherMode.CBC : CipherMode.ECB, Padding = PaddingMode.None };
                _pendingConnState.ReadAes = new AesCryptoServiceProvider() { Key = readKey, Mode = isCbc ? CipherMode.CBC : CipherMode.ECB, Padding = PaddingMode.None };
                int tmpOffset = macLen * 2 + aesKeyLen * 2;
                if (isGcm)
                {
                    _pendingConnState.WriteSalt = Utils.ReadUInt32(keyBlock, ref tmpOffset);
                    _pendingConnState.ReadSalt = Utils.ReadUInt32(keyBlock, ref tmpOffset);
                    _pendingConnState.WriteAesECB = _pendingConnState.WriteAes.CreateEncryptor(writeKey, null);
                    _pendingConnState.ReadAesECB = _pendingConnState.ReadAes.CreateEncryptor(readKey, null);
                    _pendingConnState.WriteGCMTable = GaloisCounterMode.GetH(_pendingConnState.WriteAesECB);
                    _pendingConnState.ReadGCMTable = GaloisCounterMode.GetH(_pendingConnState.ReadAesECB);
                    if (_temp512 == null)
                        _temp512 = new byte[512];
                }
                Utils.ClearArray(writeMac);
                Utils.ClearArray(readMac);
                Utils.ClearArray(writeKey);
                Utils.ClearArray(readKey);
                _pendingConnState.IvBuf = new byte[_pendingConnState.BlockLen];
                ivLen = _pendingConnState.IvLen;
            }

            _connState = _pendingConnState;
            SendHandshakeMessage(SendFinished, ref offset, ivLen);
            _handshakeData.HandshakeHash2.TransformFinalBlock(_buf, 0, 0);

            // _buf is now ready to be written to the base stream, from pos 0 to offset
            return offset;
        }

        delegate HandshakeType SendHandshakeMessageDelegate(ref int offset);

        void SendHandshakeMessage(SendHandshakeMessageDelegate func, ref int offset, int ivLen)
        {
            int start = offset;
            int messageStart = start + 5 + ivLen;

            _buf[offset++] = (byte)ContentType.Handshake;

            // Version: TLS 1.2
            _buf[offset++] = 3;
            _buf[offset++] = 3;

            // Record length to be filled in later
            offset += 2;

            offset += ivLen;

            int handshakeTypePos = offset;

            // Type and length filled in below
            offset += 4;

            var handshakeType = func(ref offset);
            var messageLen = offset - (handshakeTypePos + 4);
            _buf[handshakeTypePos] = (byte)handshakeType;
            Utils.WriteUInt24(_buf, handshakeTypePos + 1, messageLen);

            UpdateHandshakeHash(_buf, messageStart, offset - messageStart);

            offset = Encrypt(start, offset - messageStart);
        }

        void WaitForHandshakeCompleted(bool initialHandshake)
        {
            for (; ; )
            {
                if (!ReadRecord())
                {
                    _eof = true;
                    throw new IOException("Unexpected connection EOF in handshake");
                }
                Decrypt();
                if (_contentType != ContentType.ChangeCipherSpec)
                {
                    EnqueueReadData(!initialHandshake);
                }
                else
                {
                    ParseChangeCipherSpec();
                    _waitingForChangeCipherSpec = false;
                    break;
                }
            }

            while (_handshakeMessagesBuffer.Messages.Count == 0)
            {
                if (!ReadRecord())
                {
                    _eof = true;
                    throw new IOException("Unexpected connection EOF in handshake");
                }
                Decrypt();
                if (_contentType != ContentType.Handshake)
                {
                    EnqueueReadData(!initialHandshake);
                }
                else
                {
                    _handshakeMessagesBuffer.AddBytes(_buf, _plaintextStart, _plaintextLen, HandshakeMessagesBuffer.IgnoreHelloRequestsSettings.IgnoreHelloRequestsUntilFinished);
                }
            }

            if ((HandshakeType)_handshakeMessagesBuffer.Messages[0][0] == HandshakeType.Finished)
            {
                ParseFinishedMessage(_handshakeMessagesBuffer.Messages[0]);
                _handshakeMessagesBuffer.RemoveFirst(); // Leave possible hello requests after this position
            }
            else
            {
                SendAlertFatal(AlertDescription.UnexpectedMessage);
            }
        }

        #endregion

        #region Handshake messages

        HandshakeType SendClientHello(ref int offset)
        {
            _pendingConnState = new ConnectionState();
            _handshakeData = new HandshakeData();
            _handshakeData.HandshakeHash1 = new SHA256CryptoServiceProvider();
            _handshakeData.HandshakeHash2 = new SHA256CryptoServiceProvider();
            _handshakeData.HandshakeHash1_384 = new SHA384CryptoServiceProvider();
            _handshakeData.HandshakeHash2_384 = new SHA384CryptoServiceProvider();
            _handshakeData.CertificateVerifyHash_SHA1 = new SHA1CryptoServiceProvider();

            // Version: TLS 1.2
            _buf[offset++] = 3;
            _buf[offset++] = 3;

            // Client random
            var timestamp = (uint)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
            _pendingConnState.ClientRandom = new byte[32];
            _rng.GetBytes(_pendingConnState.ClientRandom);
            Utils.WriteUInt32(_pendingConnState.ClientRandom, 0, timestamp);

            Buffer.BlockCopy(_pendingConnState.ClientRandom, 0, _buf, offset, 32);
            offset += 32;

            // No session id
            _buf[offset++] = 0;

            // Cipher suites
            offset += Utils.WriteUInt16(_buf, offset, (ushort)(CipherSuiteInfo.Supported.Length * sizeof(ushort)));
            foreach (var suite in CipherSuiteInfo.Supported)
            {
                offset += Utils.WriteUInt16(_buf, offset, (ushort)suite.Id);
            }

            // Compression methods
            _buf[offset++] = 1; // Length
            _buf[offset++] = 0; // "null" compression method

            // Extensions length, fill in later
            var extensionLengthOffset = offset;
            offset += 2;

            // Renegotiation extension
            offset += Utils.WriteUInt16(_buf, offset, (ushort)ExtensionType.RenegotiationInfo);
            if (_connState.SecureRenegotiation)
            {
                // Extension length
                offset += Utils.WriteUInt16(_buf, offset, 13);

                // Renegotiated connection length
                _buf[offset++] = 12;
                // Renegotiated connection data
                Buffer.BlockCopy(_connState.ClientVerifyData, 0, _buf, offset, 12);
                offset += 12;
            }
            else
            {
                // Extension length
                offset += Utils.WriteUInt16(_buf, offset, 1);
                // Renegotiated connection length
                _buf[offset++] = 0;
            }

            // SNI extension
            if (_hostName != null)
            {
                // TODO: IDN Unicode -> Punycode

                // NOTE: IP addresses should not use SNI extension, per specification.
                System.Net.IPAddress ip;
                if (!System.Net.IPAddress.TryParse(_hostName, out ip))
                {
                    offset += Utils.WriteUInt16(_buf, offset, (ushort)ExtensionType.ServerName);
                    var byteLen = Encoding.ASCII.GetBytes(_hostName, 0, _hostName.Length, _buf, offset + 7);
                    offset += Utils.WriteUInt16(_buf, offset, (ushort)(5 + byteLen));
                    offset += Utils.WriteUInt16(_buf, offset, (ushort)(3 + byteLen));
                    _buf[offset++] = 0; // host_name
                    offset += Utils.WriteUInt16(_buf, offset, (ushort)byteLen);
                    offset += byteLen;
                }
            }

            // Signature algorithms extension. At least IIS 7.5 needs this or it immediately resets the connection.
            // Used to specify what kind of server certificate hash/signature algorithms we can use to verify it.
            offset += Utils.WriteUInt16(_buf, offset, (ushort)ExtensionType.SignatureAlgorithms);
            offset += Utils.WriteUInt16(_buf, offset, 20);
            offset += Utils.WriteUInt16(_buf, offset, 18);
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA1;
            _buf[offset++] = (byte)SignatureAlgorithm.ECDSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA256;
            _buf[offset++] = (byte)SignatureAlgorithm.ECDSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA384;
            _buf[offset++] = (byte)SignatureAlgorithm.ECDSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA512;
            _buf[offset++] = (byte)SignatureAlgorithm.ECDSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA1;
            _buf[offset++] = (byte)SignatureAlgorithm.RSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA256;
            _buf[offset++] = (byte)SignatureAlgorithm.RSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA384;
            _buf[offset++] = (byte)SignatureAlgorithm.RSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA512;
            _buf[offset++] = (byte)SignatureAlgorithm.RSA;
            _buf[offset++] = (byte)TLSHashAlgorithm.SHA1;
            _buf[offset++] = (byte)SignatureAlgorithm.DSA;

            if (CipherSuiteInfo.Supported.Any(s => s.KeyExchange == KeyExchange.ECDHE_RSA || s.KeyExchange == KeyExchange.ECDHE_ECDSA))
            {
                // Supported Elliptic Curves Extension

                offset += Utils.WriteUInt16(_buf, offset, (ushort)ExtensionType.SupportedEllipticCurves);
                offset += Utils.WriteUInt16(_buf, offset, 8);
                offset += Utils.WriteUInt16(_buf, offset, 6);
                offset += Utils.WriteUInt16(_buf, offset, (ushort)NamedCurve.secp256r1);
                offset += Utils.WriteUInt16(_buf, offset, (ushort)NamedCurve.secp384r1);
                offset += Utils.WriteUInt16(_buf, offset, (ushort)NamedCurve.secp521r1);

                // Supported Point Formats Extension

                offset += Utils.WriteUInt16(_buf, offset, (ushort)ExtensionType.SupportedPointFormats);
                offset += Utils.WriteUInt16(_buf, offset, 2);
                _buf[offset++] = 1; // Length
                _buf[offset++] = 0; // Uncompressed
            }
            

            Utils.WriteUInt16(_buf, extensionLengthOffset, (ushort)(offset - (extensionLengthOffset + 2)));

            return HandshakeType.ClientHello;
        }

        void ParseServerHelloMessage(byte[] buf, ref int pos, int endPos)
        {
            var renegotiating = _connState.ReadAes != null;

            var versionMajor = buf[pos++];
            var versionMinor = buf[pos++];
            if (versionMajor != 3 || versionMinor != 3)
            {
                SendAlertFatal(AlertDescription.ProtocolVersion);
            }

            _pendingConnState.ServerRandom = new byte[32];
            Buffer.BlockCopy(buf, pos, _pendingConnState.ServerRandom, 0, 32);
            pos += 32;

            // Skip session id
            var sessionIDLength = buf[pos++];
            pos += sessionIDLength;

            var cipherSuite = (CipherSuite)Utils.ReadUInt16(buf, ref pos);
            var compressionMethod = buf[pos++];

            _pendingConnState.CipherSuite = CipherSuiteInfo.Supported.FirstOrDefault(s => s.Id == cipherSuite);
            if (_pendingConnState.CipherSuite == null || compressionMethod != 0)
            {
                SendAlertFatal(AlertDescription.IllegalParameter);
            }

            switch (_pendingConnState.CipherSuite.PRFAlgorithm)
            {
                case PRFAlgorithm.TLSPrfSHA256:
                    _handshakeData.HandshakeHash1_384.Clear();
                    _handshakeData.HandshakeHash1_384 = null;
                    _handshakeData.HandshakeHash2_384.Clear();
                    _handshakeData.HandshakeHash2_384 = null;
                    break;
                case PRFAlgorithm.TLSPrfSHA384:
                    _handshakeData.HandshakeHash1.Clear();
                    _handshakeData.HandshakeHash1 = _handshakeData.HandshakeHash1_384;
                    _handshakeData.HandshakeHash1_384 = null;
                    _handshakeData.HandshakeHash2.Clear();
                    _handshakeData.HandshakeHash2 = _handshakeData.HandshakeHash2_384;
                    _handshakeData.HandshakeHash2_384 = null;
                    break;
                default:
                    throw new InvalidOperationException();
            }

            // If no extensions present, return
            if (pos == endPos)
                return;

            var processedRenegotiationInfo = false;

            var extensionLength = Utils.ReadUInt16(buf, ref pos);
            var extensionsEnd = pos + extensionLength;
            while (pos < extensionsEnd)
            {
                var extensionId = (ExtensionType)Utils.ReadUInt16(buf, ref pos);
                switch (extensionId)
                {
                    case ExtensionType.RenegotiationInfo:
                        if (processedRenegotiationInfo)
                            SendAlertFatal(AlertDescription.HandshakeFailure);
                        processedRenegotiationInfo = true;

                        var lengthFull = Utils.ReadUInt16(buf, ref pos);
                        var length = buf[pos++];
                        if (length + 1 != lengthFull)
                            SendAlertFatal(AlertDescription.HandshakeFailure);

                        if (!renegotiating)
                        {
                            if (length != 0)
                                SendAlertFatal(AlertDescription.HandshakeFailure);
                        }
                        if (renegotiating)
                        {
                            if (!_connState.SecureRenegotiation)
                                SendAlertFatal(AlertDescription.HandshakeFailure);

                            if (length != 24)
                                SendAlertFatal(AlertDescription.HandshakeFailure);

                            for (var j = 0; j < 12; j++)
                            {
                                if (_connState.ClientVerifyData[j] != buf[pos++])
                                    SendAlertFatal(AlertDescription.HandshakeFailure);
                            }
                            for (var j = 0; j < 12; j++)
                            {
                                if (_connState.ServerVerifyData[j] != buf[pos++])
                                    SendAlertFatal(AlertDescription.HandshakeFailure);
                            }
                        }
                        _pendingConnState.SecureRenegotiation = true;
                        break;
                    case ExtensionType.SupportedEllipticCurves:
                        var len = Utils.ReadUInt16(buf, ref pos);
                        // Contains in what formats the server can parse. Ignore it.
                        pos += len;
                        break;
                    case ExtensionType.SupportedPointFormats:
                        var length1 = Utils.ReadUInt16(buf, ref pos);
                        // Contains in what formats the server can parse. Ignore it.
                        pos += length1;
                        break;
                    case ExtensionType.ServerName:
                        var length2 = Utils.ReadUInt16(buf, ref pos);
                        pos += length2;
                        if (length2 != 0)
                            SendAlertFatal(AlertDescription.IllegalParameter);
                        break;
                    default:
                        SendAlertFatal(AlertDescription.IllegalParameter);
                        break;
                }
            }

            if (!processedRenegotiationInfo && (!renegotiating && _noRenegotiationExtensionSupportIsFatal || renegotiating && _connState.SecureRenegotiation))
            {
                SendAlertFatal(AlertDescription.HandshakeFailure);
            }
        }

        void ParseCertificateMessage(byte[] buf, ref int pos)
        {
            _handshakeData.CertList = new List<X509Certificate2>();
            _handshakeData.CertChain = new X509Chain();
            var errors = System.Net.Security.SslPolicyErrors.None;

            var totalLen = Utils.ReadUInt24(buf, ref pos);
            if (totalLen == 0)
                SendAlertFatal(AlertDescription.IllegalParameter);

            int endPos = pos + totalLen;
            while (pos < endPos)
            {
                var certLen = Utils.ReadUInt24(buf, ref pos);
                var certBytes = new byte[certLen];
                Buffer.BlockCopy(buf, pos, certBytes, 0, certLen);
                pos += certLen;

                // TODO: catch error in this constructor
                var cert = new X509Certificate2(certBytes);
                if (_handshakeData.CertList.Count != 0)
                    _handshakeData.CertChain.ChainPolicy.ExtraStore.Add(cert);
                _handshakeData.CertList.Add(cert);
            }

            // TODO: remove after Build works with mono
            //return;

            // Validate certificate
            _handshakeData.CertChain.Build(_handshakeData.CertList[0]);
            if (!string.IsNullOrEmpty(_hostName))
            {
                var ok = Utils.HostnameInCertificate(_handshakeData.CertList[0], _hostName);
                if (!ok)
                    errors |= System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
            }
            if (_handshakeData.CertChain.ChainStatus != null && _handshakeData.CertChain.ChainStatus.Length > 0)
                errors |= System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

            bool success = _remoteCertificationValidationCallback != null
                ? _remoteCertificationValidationCallback(this, _handshakeData.CertList[0], _handshakeData.CertChain, errors)
                : errors == System.Net.Security.SslPolicyErrors.None;

            if (!success)
            {
                if (_handshakeData.CertChain.ChainStatus.Any(s => (s.Status & X509ChainStatusFlags.NotTimeValid) != 0))
                    SendAlertFatal(AlertDescription.CertificateExpired);
                else if (_handshakeData.CertChain.ChainStatus.Any(s => (s.Status & X509ChainStatusFlags.Revoked) != 0))
                    SendAlertFatal(AlertDescription.CertificateRevoked);
                else if (!_handshakeData.CertChain.ChainStatus.All(s => (s.Status & ~X509ChainStatusFlags.RevocationStatusUnknown) == 0))
                {
                    //throw new Exception(string.Join(", ", _handshakeData.CertChain.ChainStatus.Select(s => s.StatusInformation)));
                    SendAlertFatal(AlertDescription.CertificateUnknown);
                }
            }
        }

        // Destroys preMasterSecret
        void SetMasterSecret(byte[] preMasterSecret)
        {
            using (var hmac = _pendingConnState.CipherSuite.CreatePrfHMAC(preMasterSecret))
            {
                var concRandom = new byte[_pendingConnState.ClientRandom.Length + _pendingConnState.ServerRandom.Length];
                Buffer.BlockCopy(_pendingConnState.ClientRandom, 0, concRandom, 0, _pendingConnState.ClientRandom.Length);
                Buffer.BlockCopy(_pendingConnState.ServerRandom, 0, concRandom, _pendingConnState.ClientRandom.Length, _pendingConnState.ServerRandom.Length);
                _pendingConnState.MasterSecret = Utils.PRF(hmac, "master secret", concRandom, 48);
            }
            Utils.ClearArray(preMasterSecret);
        }

        HandshakeType SendClientKeyExchangeRsa(ref int offset)
        {
            byte[] preMasterSecret = new byte[48];

            _rng.GetBytes(preMasterSecret);

            // TLS 1.2
            preMasterSecret[0] = 3;
            preMasterSecret[1] = 3;

            var rsa = (RSACryptoServiceProvider)_handshakeData.CertList[0].PublicKey.Key;
            var encryptedPreMasterSecret = rsa.Encrypt(preMasterSecret, false);
            SetMasterSecret(preMasterSecret);

            // Message content
            offset += Utils.WriteUInt16(_buf, offset, (ushort)encryptedPreMasterSecret.Length);
            Buffer.BlockCopy(encryptedPreMasterSecret, 0, _buf, offset, encryptedPreMasterSecret.Length);
            offset += encryptedPreMasterSecret.Length;

            return HandshakeType.ClientKeyExchange;
        }

        void ParseServerKeyExchangeMessage(byte[] buf, ref int pos)
        {
            var secParamStart = pos;

            if (_pendingConnState.CipherSuite.KeyExchange == KeyExchange.DHE_RSA || _pendingConnState.CipherSuite.KeyExchange == KeyExchange.DHE_DSS)
            {

                // We add 1 extra 0-byte to each array to make sure the sign is positive (BigInteger constructor checks most significant bit)
                var Plen = Utils.ReadUInt16(buf, ref pos);
                _handshakeData.P = new byte[Plen + 1];
                for (var i = 0; i < Plen; i++)
                    _handshakeData.P[i] = buf[pos + Plen - 1 - i];
                pos += Plen;

                var Glen = Utils.ReadUInt16(buf, ref pos);
                _handshakeData.G = new byte[Glen + 1];
                for (var i = 0; i < Glen; i++)
                    _handshakeData.G[i] = buf[pos + Glen - 1 - i];
                pos += Glen;

                var Yslen = Utils.ReadUInt16(buf, ref pos);
                _handshakeData.Ys = new byte[Yslen + 1];
                for (var i = 0; i < Yslen; i++)
                    _handshakeData.Ys[i] = buf[pos + Yslen - 1 - i];
                pos += Yslen;
            }
            else if (_pendingConnState.CipherSuite.KeyExchange == KeyExchange.ECDHE_RSA || _pendingConnState.CipherSuite.KeyExchange == KeyExchange.ECDHE_ECDSA)
            {
                // ECDHE

                var curveType = buf[pos++];
                if (curveType != 0x03) // 0x03 = Named curve
                {
                    SendAlertFatal(AlertDescription.IllegalParameter);
                }
                var namedcurve = (NamedCurve)Utils.ReadUInt16(buf, ref pos);
                EllipticCurve curve;
                switch (namedcurve)
                {
                    case NamedCurve.secp256r1:
                        curve = EllipticCurve.P256;
                        break;
                    case NamedCurve.secp384r1:
                        curve = EllipticCurve.P384;
                        break;
                    case NamedCurve.secp521r1:
                        curve = EllipticCurve.P521;
                        break;
                    default:
                        SendAlertFatal(AlertDescription.IllegalParameter);
                        curve = null;
                        break;
                }
                var opaqueLen = buf[pos++]; // TODO: check len
                if (buf[pos++] != 4) // Uncompressed
                {
                    SendAlertFatal(AlertDescription.IllegalParameter);
                }
                _handshakeData.EcX = new EllipticCurve.BigInt(buf, pos, curve.curveByteLen);
                pos += curve.curveByteLen;
                _handshakeData.EcY = new EllipticCurve.BigInt(buf, pos, curve.curveByteLen);
                pos += curve.curveByteLen;
                _handshakeData.EcCurve = curve;
            }
            else
            {
                SendAlertFatal(AlertDescription.UnexpectedMessage);
            }

            int parametersEnd = pos;

            // Digitally signed client random + server random + parameters
            var hashAlgorithm = (TLSHashAlgorithm)buf[pos++];
            var signatureAlgorithm = (SignatureAlgorithm)buf[pos++];

            var signatureLen = Utils.ReadUInt16(buf, ref pos);
            byte[] signature = new byte[signatureLen];
            Buffer.BlockCopy(buf, pos, signature, 0, signatureLen);
            pos += signatureLen;

            System.Security.Cryptography.HashAlgorithm alg = null;
            switch (hashAlgorithm)
            {
                case TLSHashAlgorithm.SHA1: alg = new SHA1CryptoServiceProvider(); break;
                case TLSHashAlgorithm.SHA256: alg = new SHA256CryptoServiceProvider(); break;
                case TLSHashAlgorithm.SHA384: alg = new SHA384CryptoServiceProvider(); break;
                case TLSHashAlgorithm.SHA512: alg = new SHA512CryptoServiceProvider(); break;
                default: SendAlertFatal(AlertDescription.IllegalParameter); break;
            }

            alg.TransformBlock(_pendingConnState.ClientRandom, 0, 32, _pendingConnState.ClientRandom, 0);
            alg.TransformBlock(_pendingConnState.ServerRandom, 0, 32, _pendingConnState.ServerRandom, 0);
            alg.TransformBlock(buf, secParamStart, parametersEnd - secParamStart, buf, secParamStart);
            alg.TransformFinalBlock(buf, 0, 0);
            var hash = alg.Hash;

            if (signatureAlgorithm == SignatureAlgorithm.ECDSA)
            {
                var pkParameters = _handshakeData.CertList[0].GetKeyAlgorithmParameters();
                var pkKey = _handshakeData.CertList[0].GetPublicKey();
                bool? res;
                if (Type.GetType("Mono.Runtime") != null)
                    res = EllipticCurve.VerifySignature(pkParameters, pkKey, hash, signature);
                else
                    res = EllipticCurve.VerifySignatureCng(pkParameters, pkKey, hash, signature);
                if (!res.HasValue)
                {
                    SendAlertFatal(AlertDescription.IllegalParameter);
                }
                else if (!res.Value)
                {
                    SendAlertFatal(AlertDescription.DecryptError);
                }
            }
            else
            {
                var pubKey = _handshakeData.CertList[0].PublicKey.Key;
                var rsa = pubKey as RSACryptoServiceProvider;
                var dsa = pubKey as DSACryptoServiceProvider;
                if (signatureAlgorithm == SignatureAlgorithm.RSA && rsa != null)
                {
                    if (!rsa.VerifyHash(hash, Utils.HashNameToOID[hashAlgorithm.ToString()], signature))
                    {
                        SendAlertFatal(AlertDescription.DecryptError);
                    }
                }
                else if (signatureAlgorithm == SignatureAlgorithm.DSA && dsa != null)
                {
                    // We must decode from DER to two raw integers
                    // NOTE: DSACryptoServiceProvider can't handle keys larger than 1024 bits, neither can SslStream.
                    var decodedSignature = Utils.DecodeDERSignature(signature, 0, signature.Length, Utils.GetHashLen(hashAlgorithm) >> 3);
                    if (!dsa.VerifyHash(hash, Utils.HashNameToOID[hashAlgorithm.ToString()], decodedSignature))
                    {
                        SendAlertFatal(AlertDescription.DecryptError);
                    }
                }
                else
                {
                    SendAlertFatal(AlertDescription.IllegalParameter);
                }
            }
        }

        HandshakeType SendClientKeyExchangeDhe(ref int offset)
        {
            if (_handshakeData.P == null)
            {
                // Server Key Exchange with DHE was not received
                SendAlertFatal(AlertDescription.UnexpectedMessage);
            }

            byte[] Xc = new byte[_handshakeData.P.Length];
            _rng.GetBytes(Xc);
            Xc[Xc.Length - 1] = 0; // Set last byte to 0 to force a positive number. The array is already 1 longer than original P.
            var gBig = new BigInteger(_handshakeData.G);
            var pBig = new BigInteger(_handshakeData.P);
            var xcBig = new BigInteger(Xc);
            
            var ycBig = BigInteger.ModPow(gBig, xcBig, pBig);
            byte[] Yctmp = ycBig.ToByteArray();
            byte[] Yc;
            if (Yctmp[Yctmp.Length - 1] == 0)
            {
                Yc = new byte[Yctmp.Length - 1];
                Buffer.BlockCopy(Yctmp, 0, Yc, 0, Yc.Length);
            }
            else
            {
                Yc = Yctmp;
            }

            var zBig = BigInteger.ModPow(new BigInteger(_handshakeData.Ys), xcBig, pBig);
            byte[] Ztmp = zBig.ToByteArray();
            byte[] Z;
            if (Ztmp[Ztmp.Length - 1] == 0)
            {
                Z = new byte[Ztmp.Length - 1];
                Buffer.BlockCopy(Ztmp, 0, Z, 0, Z.Length); // Remove last 0 byte
            }
            else
            {
                Z = Ztmp;
            }

            // Little endian -> Big endian
            Array.Reverse(Yc);
            Array.Reverse(Z);

            SetMasterSecret(Z);

            // Yc
            offset += Utils.WriteUInt16(_buf, offset, (ushort)Yc.Length);
            Buffer.BlockCopy(Yc, 0, _buf, offset, Yc.Length);
            offset += Yc.Length;

            return HandshakeType.ClientKeyExchange;
        }

        HandshakeType SendClientKeyExchangeEcdh(ref int offset)
        {
            var pkParameters = _handshakeData.CertList[0].GetKeyAlgorithmParameters();
            var pkKey = _handshakeData.CertList[0].GetPublicKey();

            var curve = EllipticCurve.GetCurveFromParameters(pkParameters);
            if (curve == null)
                SendAlertFatal(AlertDescription.IllegalParameter);

            var Qax = new EllipticCurve.BigInt(pkKey, 1, curve.curveByteLen);
            var Qay = new EllipticCurve.BigInt(pkKey, 1 + curve.curveByteLen, curve.curveByteLen);

            byte[] preMasterSecret;
            EllipticCurve.Affine publicPoint;
            curve.Ecdh(Qax, Qay, _rng, out preMasterSecret, out publicPoint);
            
            SetMasterSecret(preMasterSecret);
            _buf[offset++] = (byte)(1 + 2 * curve.curveByteLen); // Point length
            _buf[offset++] = 4; // Uncompressed
            offset += publicPoint.x.ExportToBigEndian(_buf, offset, curve.curveByteLen);
            offset += publicPoint.y.ExportToBigEndian(_buf, offset, curve.curveByteLen);
            publicPoint.Clear();

            return HandshakeType.ClientKeyExchange;
        }

        HandshakeType SendClientKeyExchangeEcdhe(ref int offset)
        {
            var ec = _handshakeData.EcCurve;
            if (ec == null)
            {
                // Server Key Exchange with ECDHE was not received
                SendAlertFatal(AlertDescription.UnexpectedMessage);
            }

            byte[] preMasterSecret;
            EllipticCurve.Affine publicPoint;
            ec.Ecdh(_handshakeData.EcX, _handshakeData.EcY, _rng, out preMasterSecret, out publicPoint);
            SetMasterSecret(preMasterSecret);

            var byteLen = ec.curveByteLen;

            _buf[offset++] = (byte)(1 + 2 * byteLen); // Point length
            _buf[offset++] = 4; // Uncompressed
            offset += publicPoint.x.ExportToBigEndian(_buf, offset, byteLen);
            offset += publicPoint.y.ExportToBigEndian(_buf, offset, byteLen);
            publicPoint.Clear();

            return HandshakeType.ClientKeyExchange;
        }

        void ParseCertificateRequest(byte[] buf, ref int pos)
        {
            int lenCertificateTypes = buf[pos++];
            var certificateTypes = new List<ClientCertificateType>();
            for (var i = 0; i < lenCertificateTypes; i++)
            {
                certificateTypes.Add((ClientCertificateType)buf[pos++]);
            }

            var lenSignatureAlgorithms = Utils.ReadUInt16(buf, ref pos);
            if ((lenSignatureAlgorithms & 1) == 1)
            {
                SendAlertFatal(AlertDescription.IllegalParameter);
            }
            var supportedSignatureAlgorithms = new List<Tuple<TLSHashAlgorithm, SignatureAlgorithm>>();
            for (var i = 0; i < lenSignatureAlgorithms; i += 2)
            {
                var ha = (TLSHashAlgorithm)buf[pos++];
                var sa = (SignatureAlgorithm)buf[pos++];
                supportedSignatureAlgorithms.Add(Tuple.Create(ha, sa));
            }

            var lenCertificateAuthorities = Utils.ReadUInt16(buf, ref pos);
            var endCertificateAuthorities = pos + lenCertificateAuthorities;
            var certificateAuthorities = new List<string>();
            while (pos < endCertificateAuthorities)
            {
                var lenCertificateAuthority = Utils.ReadUInt16(buf, ref pos);
                var certificateAuthorityBytes = new byte[lenCertificateAuthority];
                Buffer.BlockCopy(buf, pos, certificateAuthorityBytes, 0, lenCertificateAuthority);
                pos += lenCertificateAuthority;
                var certificateAuthority = new X500DistinguishedName(certificateAuthorityBytes).Name;
                certificateAuthorities.Add(certificateAuthority);
            }

            _handshakeData.CertificateTypes = certificateTypes;
            _handshakeData.SupportedSignatureAlgorithms = supportedSignatureAlgorithms;
            _handshakeData.CertificateAuthorities = certificateAuthorities;
        }
        
        HandshakeType SendClientCertificate(ref int offset)
        {
            X509Chain selected = null;
            if (_clientCertificates != null)
            {
                foreach (var cert in _clientCertificates)
                {
                    var cert2 = cert as X509Certificate2;
                    if (cert2 == null)
                        cert2 = new X509Certificate2(cert);
                    var chain = new X509Chain();
                    chain.Build(cert2);
                    foreach (var elem in chain.ChainElements)
                    {
                        if (_handshakeData.CertificateAuthorities.Contains(elem.Certificate.Issuer))
                            selected = chain;
                    }
                    if (selected != null)
                        break;
                }
            }

            if (selected == null)
            {
                // Did not find any good certificate, send an empty list...
                offset += Utils.WriteUInt24(_buf, offset, 0);
            }
            else
            {
                // We send the user's certificate and additional parent certificates found in the machine/user certificate store
                var byteEncoded = new byte[selected.ChainElements.Count][];
                for (var i = 0; i < selected.ChainElements.Count; i++)
                {
                    byteEncoded[i] = selected.ChainElements[i].Certificate.Export(X509ContentType.Cert);
                }
                offset += Utils.WriteUInt24(_buf, offset, byteEncoded.Sum(a => 3 + a.Length));

                foreach (var arr in byteEncoded)
                {
                    offset += Utils.WriteUInt24(_buf, offset, arr.Length);
                    Buffer.BlockCopy(arr, 0, _buf, offset, arr.Length);
                    offset += arr.Length;
                }
            }
            _handshakeData.SelectedClientCertificate = selected;

            return HandshakeType.Certificate;
        }

        HandshakeType SendCertificateVerify(ref int offset)
        {
            var key = new X509Certificate2(_clientCertificates[0]).PrivateKey;

            var keyDsa = key as DSACryptoServiceProvider;
            var keyRsa = key as RSACryptoServiceProvider;
            
            byte[] signature = null, hash = null;

            if (keyDsa != null)
            {
                if (!_handshakeData.SupportedSignatureAlgorithms.Contains(Tuple.Create(TLSHashAlgorithm.SHA1, SignatureAlgorithm.DSA)))
                {
                    SendAlertFatal(AlertDescription.HandshakeFailure);
                }
                _handshakeData.CertificateVerifyHash_SHA1.TransformFinalBlock(_buf, 0, 0);
                hash = _handshakeData.CertificateVerifyHash_SHA1.Hash;
                signature = keyDsa.SignHash(hash, Utils.HashNameToOID["SHA1"]);

                // Convert to DER
                var r = new byte[21];
                var s = new byte[21];
                Array.Reverse(signature, 0, 20);
                Array.Reverse(signature, 20, 20);
                Buffer.BlockCopy(signature, 0, r, 0, 20);
                Buffer.BlockCopy(signature, 20, s, 0, 20);
                r = new BigInteger(r).ToByteArray();
                s = new BigInteger(s).ToByteArray();
                Array.Reverse(r);
                Array.Reverse(s);

                signature = new byte[r.Length + s.Length + 6];
                signature[0] = 0x30; // SEQUENCE
                signature[1] = (byte)(signature.Length - 2);
                signature[2] = 0x02; // INTEGER
                signature[3] = (byte)r.Length;
                Buffer.BlockCopy(r, 0, signature, 4, r.Length);
                signature[4 + r.Length] = 0x02; // INTEGER
                signature[4 + r.Length + 1] = (byte)s.Length;
                Buffer.BlockCopy(s, 0, signature, 4 + r.Length + 2, s.Length);

                _buf[offset++] = (byte)TLSHashAlgorithm.SHA1;
                _buf[offset++] = (byte)SignatureAlgorithm.DSA;
            }
            else if (keyRsa != null)
            {
                if (!_handshakeData.SupportedSignatureAlgorithms.Contains(Tuple.Create(TLSHashAlgorithm.SHA1, SignatureAlgorithm.RSA)))
                {
                    SendAlertFatal(AlertDescription.HandshakeFailure);
                }

                // NOTE: It seems problematic to support other hash algorithms than SHA1 since the PrivateKey included in the certificate
                // often uses an old Crypto Service Provider (Microsoft Base Cryptographic Provider v1.0) instead of Microsoft Enhanced RSA and AES Cryptographic Provider.
                // The following out-commented code might work to change CSP.

                //var csp = new RSACryptoServiceProvider().CspKeyContainerInfo;
                //keyRsa = new RSACryptoServiceProvider(new CspParameters(csp.ProviderType, csp.ProviderName, keyRsa.CspKeyContainerInfo.KeyContainerName));

                _handshakeData.CertificateVerifyHash_SHA1.TransformFinalBlock(_buf, 0, 0);
                hash = _handshakeData.CertificateVerifyHash_SHA1.Hash;
                
                signature = keyRsa.SignHash(hash, Utils.HashNameToOID["SHA1"]);

                _buf[offset++] = (byte)TLSHashAlgorithm.SHA1;
                _buf[offset++] = (byte)SignatureAlgorithm.RSA;
            }
            else
            {
                SendAlertFatal(AlertDescription.HandshakeFailure);
            }
            _handshakeData.CertificateVerifyHash_SHA1.Clear();
            _handshakeData.CertificateVerifyHash_SHA1 = null;

            key.Dispose();

            offset += Utils.WriteUInt16(_buf, offset, (ushort)signature.Length);
            Buffer.BlockCopy(signature, 0, _buf, offset, signature.Length);
            offset += signature.Length;

            return HandshakeType.CertificateVerify;
        }

        void SendChangeCipherSpec(ref int offset, int ivLen)
        {
            _buf[offset++] = (byte)ContentType.ChangeCipherSpec;
            
            // Version: TLS 1.2
            _buf[offset++] = 3;
            _buf[offset++] = 3;

            offset += 2;

            offset += ivLen;

            // Content
            _buf[offset++] = 1;
        }

        HandshakeType SendFinished(ref int offset)
        {
            using (var hmac = _connState.CipherSuite.CreatePrfHMAC(_connState.MasterSecret))
            {
                _handshakeData.HandshakeHash1.TransformFinalBlock(_buf, 0, 0);
                byte[] inputHash = _handshakeData.HandshakeHash1.Hash;
                byte[] hash = Utils.PRF(hmac, "client finished", inputHash, 12);
                Buffer.BlockCopy(hash, 0, _buf, offset, 12);
                offset += 12;
                if (_connState.SecureRenegotiation)
                    _connState.ClientVerifyData = hash;
                else
                    Utils.ClearArray(hash);
                Utils.ClearArray(inputHash);
            }
            _handshakeData.HandshakeHash1.Clear();
            _handshakeData.HandshakeHash1 = null;

            return HandshakeType.Finished;
        }

        void ParseChangeCipherSpec()
        {
            if (_plaintextLen != 1 || _buf[_plaintextStart] != 1)
                SendAlertFatal(AlertDescription.IllegalParameter);
            if (_pendingConnState != _connState)
                SendAlertFatal(AlertDescription.UnexpectedMessage);

            // We don't accept buffered handshake messages to be completed after Change Cipher Spec,
            // since they would not be encrypted correctly
            if (_handshakeMessagesBuffer.HasBufferedData)
                SendAlertFatal(AlertDescription.UnexpectedMessage);

            _readConnState.Dispose();
            _readConnState = _connState;
            _pendingConnState = null;
        }

        void ParseFinishedMessage(byte[] buf)
        {
            using (var hmac = _connState.CipherSuite.CreatePrfHMAC(_connState.MasterSecret))
            {
                byte[] hash = Utils.PRF(hmac, "server finished", _handshakeData.HandshakeHash2.Hash, 12);
                if (buf.Length != 4 + 12 || !hash.SequenceEqual(buf.Skip(4)))
                    SendAlertFatal(AlertDescription.DecryptError);
                if (_connState.SecureRenegotiation)
                    _connState.ServerVerifyData = hash;
            }
            _handshakeData = null;
        }

        #endregion

        #region Alerts

        void SendAlertFatal(AlertDescription description, string message = null)
        {
            throw new ClientAlertException(description, message);
        }

        void WriteAlertFatal(AlertDescription description)
        {
            _buf[0] = (byte)ContentType.Alert;
            _buf[1] = 3;
            _buf[2] = 3;
            _buf[5 + _connState.IvLen] = (byte)AlertLevel.Fatal;
            _buf[5 + _connState.IvLen + 1] = (byte)description;
            int endPos = Encrypt(0, 2);
            _baseStream.Write(_buf, 0, endPos);
            _baseStream.Flush();
            _baseStream.Close();
            _eof = true;
            _closed = true;
            _connState.Dispose();
            if (_pendingConnState != null)
                _pendingConnState.Dispose();
            if (_temp512 != null)
                Utils.ClearArray(_temp512);
        }

        void SendClosureAlert()
        {
            _buf[0] = (byte)ContentType.Alert;
            _buf[1] = 3;
            _buf[2] = 3;
            _buf[5 + _connState.IvLen] = (byte)AlertLevel.Warning;
            _buf[5 + _connState.IvLen + 1] = (byte)AlertDescription.CloseNotify;
            int endPos = Encrypt(0, 2);
            _baseStream.Write(_buf, 0, endPos);
            _baseStream.Flush();
        }

        void HandleAlertMessage()
        {
            if (_plaintextLen != 2)
                SendAlertFatal(AlertDescription.DecodeError);

            var alertLevel = (AlertLevel)_buf[_plaintextStart];
            var alertDescription = (AlertDescription)_buf[_plaintextStart + 1];

            switch (alertDescription)
            {
                case AlertDescription.CloseNotify:
                    _eof = true;
                    try
                    {
                        SendClosureAlert();
                    }
                    catch (IOException)
                    {
                        // Don't care about this fails (the other end has closed the connection so we couldn't write)
                    }

                    // Now, did the stream end normally (end of stream) or was the connection reset?
                    // We read 0 bytes to find out. If end of stream, it will just return 0, otherwise an exception will be thrown, as we want.
                    // TODO: what to do with _closed? (_eof is true)
                    _baseStream.Read(_buf, 0, 0);
                    
                    _baseStream.Close();
                    break;
                default:
                    if (alertLevel == AlertLevel.Fatal)
                    {
                        _eof = true;
                        _baseStream.Close();
                        Dispose();
                        throw new IOException("TLS Fatal alert: " + alertDescription);
                    }
                    break;
            }
        }

        #endregion

        void CheckCanWrite()
        {
            if (_readStart != _readEnd)
            {
                throw new IOException("Cannot write data until everything buffered has been read");
            }
            _readStart = _readEnd = 0;
        }

        void EnqueueReadData(bool allowApplicationData)
        {
            if (_contentType == ContentType.ApplicationData && allowApplicationData)
            {
                if (_bufferedReadData == null)
                    _bufferedReadData = new Queue<byte[]>();

                if (_lenBufferedReadData + _plaintextLen <= MaxBufferedReadData)
                {
                    var bytes = new byte[_plaintextLen];
                    Buffer.BlockCopy(_buf, _plaintextStart, bytes, 0, _plaintextLen);
                    _bufferedReadData.Enqueue(bytes);
                    return;
                }
            }
            else if (_contentType == ContentType.Alert)
            {
                HandleAlertMessage();
            }
            SendAlertFatal(AlertDescription.UnexpectedMessage);
        }

        public void PerformInitialHandshake(string hostName, X509CertificateCollection clientCertificates, System.Net.Security.RemoteCertificateValidationCallback remoteCertificateValidationCallback)
        {
            if (_connState.CipherSuite != null || _pendingConnState != null)
                throw new InvalidOperationException("Already performed initial handshake");

            _hostName = hostName;
            _clientCertificates = clientCertificates;
            _remoteCertificationValidationCallback = remoteCertificateValidationCallback;

            try
            {
                int offset = 0;
                SendHandshakeMessage(SendClientHello, ref offset, 0);
                _baseStream.Write(_buf, 0, offset);
                _baseStream.Flush();
                GetInitialHandshakeMessages();

                var keyExchange = _connState.CipherSuite.KeyExchange;
                if (keyExchange == KeyExchange.RSA || keyExchange == KeyExchange.ECDH_ECDSA || keyExchange == KeyExchange.ECDH_RSA)
                {
                    // Don't use false start for non-forward-secrecy key exchanges; we have to wait for the finished message and verify it

                    WaitForHandshakeCompleted(true);
                }
            }
            catch (ClientAlertException e)
            {
                WriteAlertFatal(e.Description);
                throw;
            }
        }

        int WriteSpaceLeft { get { return _buf.Length - _writePos - (_connState.CipherSuite.AesMode == AesMode.CBC ? _connState.MacLen + 16 /*max padding*/ : 16); } }

        #region Stream overrides

        public override void Write(byte[] buffer, int offset, int len)
        {
            if (_connState.CipherSuite == null)
            {
                throw new InvalidOperationException("Must perform initial handshake before writing application data");
            }
            try
            {
                if (_pendingConnState != null && !_waitingForChangeCipherSpec)
                {
                    // OpenSSL violates TLS 1.2 spec by not allowing interleaved application data and handshake data,
                    // so we wait for the handshake to complete
                    GetInitialHandshakeMessages(true);

                    // For simplicity, don't try to do a "false start" here, so wait until Finished has been received
                    WaitForHandshakeCompleted(false);
                }
                CheckCanWrite();
                for (; ; )
                {
                    int toWrite = Math.Min(WriteSpaceLeft, len);
                    Buffer.BlockCopy(buffer, offset, _buf, _writePos, toWrite);
                    _writePos += toWrite;
                    offset += toWrite;
                    len -= toWrite;
                    if (len == 0)
                    {
                        return;
                    }
                    Flush();
                }
            }
            catch (ClientAlertException e)
            {
                WriteAlertFatal(e.Description);
                throw;
            }
        }

        public override void Flush()
        {
            if (_writePos > 5 + _connState.IvLen)
            {
                try
                {
                    _buf[0] = (byte)ContentType.ApplicationData;
                    _buf[1] = 3;
                    _buf[2] = 3;
                    int endPos = Encrypt(0, _writePos - 5 - _connState.IvLen);
                    _baseStream.Write(_buf, 0, endPos);
                    _baseStream.Flush();
                    _writePos = 5 + _connState.IvLen;
                }
                catch (ClientAlertException e)
                {
                    WriteAlertFatal(e.Description);
                    throw;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int len)
        {
            try
            {
                for (; ; )
                {
                    // Handshake messages take priority over application data
                    if (_handshakeMessagesBuffer.Messages.Count > 0)
                    {
                        if (_waitingForFinished)
                        {
                            if (_handshakeMessagesBuffer.Messages[0][0] != (byte)HandshakeType.Finished)
                            {
                                SendAlertFatal(AlertDescription.UnexpectedMessage);
                            }
                            ParseFinishedMessage(_handshakeMessagesBuffer.Messages[0]);
                            _waitingForFinished = false;
                            _handshakeMessagesBuffer.RemoveFirst(); // There may be Hello Requests after Finished
                        }
                        if (_handshakeMessagesBuffer.Messages.Count > 0)
                        {
                            if (_pendingConnState == null)
                            {
                                // Not currently renegotiating, should be a hello request
                                if (_handshakeMessagesBuffer.Messages.Any(m => !HandshakeMessagesBuffer.IsHelloRequest(m)))
                                {
                                    SendAlertFatal(AlertDescription.UnexpectedMessage);
                                }
                                _renegotiationTempWriteBuf = new byte[_buf.Length];
                                byte[] bufSaved = _buf;
                                _buf = _renegotiationTempWriteBuf;
                                int writeOffset = 0;
                                SendHandshakeMessage(SendClientHello, ref writeOffset, _connState.IvLen);
                                _baseStream.Write(_buf, 0, writeOffset);
                                _baseStream.Flush();
                                _buf = bufSaved;

                                _handshakeMessagesBuffer.ClearMessages();
                            }
                            else
                            {
                                // Ignore hello request messages when we are renegotiating,
                                // by setting ignoreHelloRequests to false below in AddBytes, if _pendingConnState != null
                                if (_waitingForChangeCipherSpec)
                                {
                                    SendAlertFatal(AlertDescription.UnexpectedMessage);
                                }
                                if (_handshakeMessagesBuffer.HasServerHelloDone)
                                {
                                    byte[] bufSaved = _buf;
                                    _buf = _renegotiationTempWriteBuf;
                                    var responseLen = TraverseHandshakeMessages();

                                    _baseStream.Write(_buf, 0, responseLen);
                                    _baseStream.Flush();
                                    _writePos = 5 + _pendingConnState.IvLen;
                                    _waitingForChangeCipherSpec = true;

                                    _buf = bufSaved;
                                    _renegotiationTempWriteBuf = null;
                                }
                            }
                        }
                    }
                    if (_bufferedReadData != null)
                    {
                        var buf = _bufferedReadData.Peek();
                        var toRead = Math.Min(buf.Length - _posBufferedReadData, len);
                        Buffer.BlockCopy(buf, _posBufferedReadData, buffer, offset, toRead);
                        _posBufferedReadData += toRead;
                        _lenBufferedReadData -= toRead;
                        if (_posBufferedReadData == buf.Length)
                        {
                            _bufferedReadData.Dequeue();
                            _posBufferedReadData = 0;
                            if (_bufferedReadData.Count == 0)
                            {
                                _bufferedReadData = null;
                            }
                        }
                        return toRead;
                    }
                    if (_decryptedReadPos < _decryptedReadEnd)
                    {
                        var toRead = Math.Min(_decryptedReadEnd - _decryptedReadPos, len);
                        Buffer.BlockCopy(_buf, _decryptedReadPos, buffer, offset, toRead);
                        _decryptedReadPos += toRead;
                        return toRead;
                    }
                    if (_eof)
                        return 0;
                    if (!ReadRecord())
                    {
                        _eof = true;
                        return 0;
                    }
                    Decrypt();

                    if (_contentType == ContentType.ApplicationData)
                    {
                        if (_readConnState.ReadAes == null || _waitingForFinished)
                        {
                            // Bad state, cannot read application data with null cipher, or until finished has been received
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        }
                        _decryptedReadPos = _plaintextStart;
                        _decryptedReadEnd = _decryptedReadPos + _plaintextLen;
                        continue;
                    }
                    else if (_contentType == ContentType.ChangeCipherSpec)
                    {
                        ParseChangeCipherSpec();
                        _waitingForChangeCipherSpec = false;
                        _waitingForFinished = true;
                        continue;
                    }
                    else if (_contentType == ContentType.Handshake)
                    {
                        _handshakeMessagesBuffer.AddBytes(_buf, _plaintextStart, _plaintextLen, _pendingConnState != null ?
                            HandshakeMessagesBuffer.IgnoreHelloRequestsSettings.IgnoreHelloRequestsUntilFinished :
                            HandshakeMessagesBuffer.IgnoreHelloRequestsSettings.IncludeHelloRequests);
                        if (_handshakeMessagesBuffer.Messages.Count > 5)
                        {
                            // There can never be more than 5 handshake messages in a handshake
                            SendAlertFatal(AlertDescription.UnexpectedMessage);
                        }
                        // The handshake message(s) will be processed in the loop's next iteration
                    }
                    else if (_contentType == ContentType.Alert)
                    {
                        HandleAlertMessage();
                    }
                    else
                    {
                        SendAlertFatal(AlertDescription.UnexpectedMessage);
                    }
                }
            }
            catch (ClientAlertException e)
            {
                WriteAlertFatal(e.Description);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            // NOTE: If currently in the middle of a handshake, we send closure alert. That should be ok.
            if (!_closed && disposing)
            {
                try
                {
                    if (!_eof)
                    {
                        // TODO: Ok or not if this throws an exception?

                        SendClosureAlert();
                        _baseStream.Close();
                    }
                }
                finally
                {
                    _closed = true;
                    _connState.Dispose();
                    if (_pendingConnState != null)
                        _pendingConnState.Dispose();
                    if (_temp512 != null)
                        Utils.ClearArray(_temp512);
                }
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }
        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }
        public override long Length
        {
            get { throw new NotSupportedException(); }
        }
        public override bool CanRead
        {
            get { return _connState.IsAuthenticated && _baseStream.CanRead; }
        }
        public override bool CanWrite
        {
            get { return _connState.IsAuthenticated && _baseStream.CanWrite; }
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
        public override bool CanSeek
        {
            get { return false; }
        }

        #endregion

        public bool HasBufferedReadData()
        {
            if (_lenBufferedReadData > 0)
                return true;
            if (_decryptedReadPos < _decryptedReadEnd)
                return true;
            if (_readStart == _readEnd)
                return false;

            // Otherwise there are buffered unprocessed packets. We check if any of them is application data.
            int pos = _readStart;
            for (; ; )
            {
                if ((ContentType)_buf[pos] == ContentType.ApplicationData)
                    return true;
                if (pos + 5 >= _readEnd)
                    return false;
                pos += 3;
                int recordLen = Utils.ReadUInt16(_buf, ref pos);
                pos += recordLen;
                if (pos >= _readEnd)
                    return false;
            }
        }
    }
}