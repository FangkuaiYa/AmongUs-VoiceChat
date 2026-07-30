// Package server implements the Interstellar Voice Server core.
package server

import (
	"encoding/binary"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"

	"github.com/interstellar/voice-server/internal/audio"
	"github.com/interstellar/voice-server/internal/protocol"
)

// Constants
const (
	// Write timeout for WebSocket writes.
	writeTimeout = 5 * time.Second

	// Max audio frames to queue per client before dropping.
	maxAudioQueue = 32

	// Read buffer size for WebSocket messages.
	readBufferSize = 65536

	// Ping interval for keep-alive.
	pingInterval = 15 * time.Second

	// Max time without pong before disconnect.
	pongTimeout = 45 * time.Second
)

// Client represents a connected voice chat client.
type Client struct {
	conn   *websocket.Conn
	room   *Room
	server *Server

	// Identity
	clientID   byte
	playerName string
	playerID   byte

	// State
	isMuted         atomic.Bool
	isImpostorRadio atomic.Bool
	closed          atomic.Bool

	// Audio
	seqNum       atomic.Uint32
	audioTx      *audio.Throughput
	audioRx      *audio.Throughput
	audioQueue   chan *protocol.AudioFrame
	jitterBuffer *audio.JitterBuffer // per-source reordering
	jitterMu     sync.Mutex

	// Rate limiting
	rateLimiter *audio.RateLimiter

	// Write synchronization
	writeMu  sync.Mutex
	writeBuf *protocol.FrameWriter

	// Timing
	lastPong atomic.Int64 // unix nano
	joinedAt time.Time

	logger *log.Logger
}

// newClient creates a new Client.
func newClient(conn *websocket.Conn, srv *Server) *Client {
	c := &Client{
		conn:         conn,
		server:       srv,
		audioTx:      &audio.Throughput{},
		audioRx:      &audio.Throughput{},
		audioQueue:   make(chan *protocol.AudioFrame, maxAudioQueue),
		jitterBuffer: audio.NewJitterBuffer(5), // 5-frame jitter buffer (~100ms)
		writeBuf:     protocol.NewFrameWriter(4096),
		joinedAt:     time.Now(),
		logger:       log.Default(),
	}
	if srv.config.MaxBandwidthPerClient > 0 {
		c.rateLimiter = audio.NewRateLimiter(
			srv.config.MaxBandwidthPerClient,
			srv.config.MaxBandwidthPerClient*2,
		)
	}
	return c
}

// Send writes a raw binary message to the WebSocket. Applies encryption if configured.
func (c *Client) Send(data []byte) error {
	if c.closed.Load() {
		return nil
	}

	// Encrypt if cipher is configured
	if c.server.cipher != nil {
		var err error
		data, err = c.server.cipher.EncryptFrame(data)
		if err != nil {
			c.logger.Printf("client %d: encrypt error: %v", c.clientID, err)
			return err
		}
	}

	// Bandwidth check (only for audio-heavy messages)
	if c.rateLimiter != nil && len(data) > 100 {
		if !c.rateLimiter.Allow(len(data)) {
			return nil // silently drop
		}
	}

	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
	return c.conn.WriteMessage(websocket.BinaryMessage, data)
}

// SendTagged sends a single tagged message.
func (c *Client) SendTagged(data []byte) error {
	c.writeMu.Lock()
	c.writeBuf.Reset()
	c.writeBuf.WriteTagged(data)
	frame := c.writeBuf.Bytes()
	c.writeMu.Unlock()
	return c.Send(frame)
}

// SendJoinResponse sends the join response with assigned ID and existing clients.
func (c *Client) SendJoinResponse(existingClients []protocol.ClientInfo, hostSettings []byte) error {
	var buf bytesBuffer
	protocol.EncodeJoinResponse(&buf, protocol.JoinResponse{
		YourClientID: c.clientID,
		Clients:      existingClients,
		HostSettings: hostSettings,
	})
	return c.SendTagged(buf.Bytes())
}

// SendProfileShare sends another client's profile to this client.
func (c *Client) SendProfileShare(clientID byte, name string, pid byte) error {
	var buf bytesBuffer
	protocol.EncodeProfileShare(&buf, protocol.ProfileShareMsg{
		ClientID:   clientID,
		PlayerName: name,
		PlayerID:   pid,
	})
	return c.SendTagged(buf.Bytes())
}

// SendMuteShare sends another client's mute status.
func (c *Client) SendMuteShare(clientID byte, muted, impostorRadio bool) error {
	var buf bytesBuffer
	protocol.EncodeMuteShare(&buf, protocol.MuteShareMsg{
		ClientID:        clientID,
		IsMuted:         muted,
		IsImpostorRadio: impostorRadio,
	})
	return c.SendTagged(buf.Bytes())
}

// SendClientLeft notifies this client that another client left.
func (c *Client) SendClientLeft(clientID byte) error {
	var buf bytesBuffer
	protocol.EncodeLeave(&buf, protocol.LeaveMsg{ClientID: clientID})
	return c.SendTagged(buf.Bytes())
}

// SendHostSettings sends host settings to this client.
func (c *Client) SendHostSettings(raw []byte) error {
	var buf bytesBuffer
	protocol.EncodeHostSettings(&buf, raw)
	return c.SendTagged(buf.Bytes())
}

// SendServerInfo sends server info to this client.
func (c *Client) SendServerInfo(info protocol.ServerInfoMsg) error {
	var buf bytesBuffer
	protocol.EncodeServerInfo(&buf, info)
	return c.SendTagged(buf.Bytes())
}

// SendCustomData sends custom data to this client.
func (c *Client) SendCustomData(raw []byte) error {
	var buf bytesBuffer
	protocol.EncodeCustomData(&buf, raw)
	return c.SendTagged(buf.Bytes())
}

// SendAudioFrame sends an audio frame to this client.
func (c *Client) SendAudioFrame(frame protocol.AudioFrame) error {
	var buf bytesBuffer
	protocol.EncodeAudioFrame(&buf, frame)
	return c.SendTagged(buf.Bytes())
}

// EnqueueAudio adds an audio frame to the send queue. Drops if queue is full.
func (c *Client) EnqueueAudio(frame *protocol.AudioFrame) {
	select {
	case c.audioQueue <- frame:
	default:
		// Queue full — drop oldest
		select {
		case <-c.audioQueue:
		default:
		}
		select {
		case c.audioQueue <- frame:
		default:
		}
	}
}

// runAudioSender is the audio send loop goroutine.
func (c *Client) runAudioSender() {
	for frame := range c.audioQueue {
		if c.closed.Load() {
			continue
		}
		if err := c.SendAudioFrame(*frame); err != nil {
			c.logger.Printf("client %d: audio send error: %v", c.clientID, err)
			return
		}
		c.audioRx.Add(len(frame.OpusData) + 8)
	}
}

// runPinger sends periodic ping frames and checks pong responses.
func (c *Client) runPinger() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()

	for range ticker.C {
		if c.closed.Load() {
			return
		}

		c.writeMu.Lock()
		c.conn.SetWriteDeadline(time.Now().Add(writeTimeout))
		err := c.conn.WriteMessage(websocket.PingMessage, nil)
		c.writeMu.Unlock()

		if err != nil {
			c.logger.Printf("client %d: ping error: %v", c.clientID, err)
			c.Close()
			return
		}

		// Check last pong
		lastPong := time.Unix(0, c.lastPong.Load())
		if time.Since(lastPong) > pongTimeout && !lastPong.IsZero() {
			c.logger.Printf("client %d: pong timeout", c.clientID)
			c.Close()
			return
		}
	}
}

// Close disconnects the client and cleans up resources.
func (c *Client) Close() {
	if !c.closed.CompareAndSwap(false, true) {
		return
	}
	close(c.audioQueue)
	c.jitterBuffer.Reset()
	c.conn.Close()
	if c.room != nil {
		c.room.removeClient(c)
	}
	c.logger.Printf("client %d: disconnected (name=%s, pid=%d)", c.clientID, c.playerName, c.playerID)
}

// readLoop reads messages from the WebSocket connection.
func (c *Client) readLoop() {
	defer c.Close()

	c.conn.SetReadLimit(readBufferSize)
	c.conn.SetReadDeadline(time.Now().Add(pongTimeout))
	c.conn.SetPongHandler(func(string) error {
		c.lastPong.Store(time.Now().UnixNano())
		c.conn.SetReadDeadline(time.Now().Add(pongTimeout))
		return nil
	})

	for {
		msgType, data, err := c.conn.ReadMessage()
		if err != nil {
			if !c.closed.Load() {
				c.logger.Printf("client %d: read error: %v", c.clientID, err)
			}
			return
		}

		if msgType != websocket.BinaryMessage {
			continue
		}

		c.handleMessage(data)
	}
}

// handleMessage processes an incoming WebSocket message.
func (c *Client) handleMessage(data []byte) {
	// Decrypt if cipher is configured
	if c.server.cipher != nil {
		var err error
		data, err = c.server.cipher.DecryptFrame(data)
		if err != nil {
			// Silent drop — could be an unencrypted client connecting to encrypted server
			return
		}
	}

	if len(data) < 2 {
		return
	}

	// New format — length-prefixed frame
	reader, err := protocol.NewFrameReader(data)
	if err != nil {
		c.logger.Printf("client %d: frame parse error: %v (first bytes: %x)", c.clientID, err, data[:min(4, len(data))])
		return
	}
	for {
		tagged := reader.Next()
		if tagged == nil {
			break
		}
		if len(tagged) == 0 {
			continue
		}
		c.dispatchMessage(tagged[0], tagged[1:])
	}
}

// dispatchMessage dispatches a single message by type. Returns bytes consumed.
func (c *Client) dispatchMessage(msgType byte, payload []byte) int {
	switch msgType {
	case protocol.TypeJoinRequest:
		req, err := protocol.DecodeJoinRequest(payload)
		if err != nil {
			return -1
		}
		c.server.handleJoin(c, req)
		return len(payload)

	case protocol.TypeProfile:
		profile, err := protocol.DecodeProfile(payload)
		if err != nil {
			return -1
		}
		c.playerName = profile.PlayerName
		c.playerID = profile.PlayerID
		if c.room != nil {
			c.room.broadcastProfile(c, profile.PlayerName, profile.PlayerID)
		}
		return len(payload)

	case protocol.TypeMuteStatus:
		if len(payload) < 1 {
			return -1
		}
		c.isMuted.Store(payload[0]&1 != 0)
		c.isImpostorRadio.Store(payload[0]&2 != 0)
		if c.room != nil {
			c.room.broadcastMuteStatus(c, payload[0]&1 != 0, payload[0]&2 != 0)
		}
		return 1

	case protocol.TypeAudioData:
		return c.handleAudioData(payload)

	case protocol.TypeHostSettings:
		if c.room != nil {
			c.room.setHostSettings(payload)
			c.room.broadcastHostSettings(c, payload)
		}
		return len(payload)

	case protocol.TypeCustomData:
		if c.room != nil {
			c.room.broadcastCustomData(c, payload)
		}
		return len(payload)

	case protocol.TypePing:
		var buf bytesBuffer
		protocol.EncodePong(&buf)
		c.SendTagged(buf.Bytes())
		return 0

	case protocol.TypePong:
		return 0

	default:
		return len(payload) // skip unknown
	}
}

// handleAudioData processes an incoming audio frame with jitter buffering.
func (c *Client) handleAudioData(payload []byte) int {
	if len(payload) < 7 {
		return -1
	}

	pos := 0
	_ = payload[pos] // srcID — already known from c.clientID, skip
	pos++
	seq := binary.BigEndian.Uint32(payload[pos:])
	pos += 4
	dur := binary.BigEndian.Uint16(payload[pos:])
	pos += 2
	opusData := payload[pos:]

	if len(opusData) <= 2 {
		return len(payload)
	}

	// Skip if muted
	if c.isMuted.Load() {
		return len(payload)
	}

	// Track audio TX
	c.audioTx.Add(len(opusData) + 8)

	// Build frame
	frame := &protocol.AudioFrame{
		SourceID:       c.clientID,
		SequenceNumber: c.seqNum.Add(1),
		DurationRTP:    dur,
		OpusData:       make([]byte, len(opusData)),
	}
	copy(frame.OpusData, opusData)

	// Use sender's sequence number for ordering, forward with our sequence number
	_ = seq // sender's seq used only for jitter buffer ordering

	// Encode frame for jitter buffer input
	var buf bytesBuffer
	protocol.EncodeAudioFrame(&buf, *frame)
	tagged := buf.Bytes()

	// Push through jitter buffer
	c.jitterMu.Lock()
	ordered := c.jitterBuffer.Push(frame.SequenceNumber, tagged)
	c.jitterMu.Unlock()

	// Relay ordered frames to room
	if c.room != nil {
		for _, t := range ordered {
			c.relayOrderedAudio(t)
		}
	}

	return len(payload)
}

// relayOrderedAudio relays a jitter-buffer-ordered audio frame to the room.
func (c *Client) relayOrderedAudio(tagged []byte) {
	if len(tagged) < 8 {
		return
	}
	// Skip type byte (already processed by dispatchMessage)
	frame, _, err := protocol.DecodeAudioFrame(tagged[1:])
	if err != nil {
		return
	}

	if c.room != nil {
		c.room.broadcastAudio(c, &frame)
	}
}

// --- Helper: simple bytes.Buffer wrapper ---

type bytesBuffer struct {
	buf []byte
}

func (b *bytesBuffer) Write(p []byte) (int, error) {
	b.buf = append(b.buf, p...)
	return len(p), nil
}

func (b *bytesBuffer) Bytes() []byte {
	return b.buf
}
