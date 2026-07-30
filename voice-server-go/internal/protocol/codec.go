package protocol

import (
	"encoding/binary"
	"errors"
	"io"
)

// Common errors.
var (
	ErrPayloadTooLarge = errors.New("payload exceeds maximum size")
	ErrInvalidLength   = errors.New("invalid payload length")
	ErrBufferTooSmall  = errors.New("buffer too small")
)

// --- JoinRequest ---

type JoinRequest struct {
	RoomCode string
	Region   string
}

func (m *JoinRequest) Type() byte { return TypeJoinRequest }

func EncodeJoinRequest(w io.Writer, req JoinRequest) error {
	buf := make([]byte, 1+2+len(req.RoomCode)+2+len(req.Region))
	buf[0] = TypeJoinRequest
	n := 1
	binary.BigEndian.PutUint16(buf[n:], uint16(len(req.RoomCode)))
	n += 2
	copy(buf[n:], req.RoomCode)
	n += len(req.RoomCode)
	binary.BigEndian.PutUint16(buf[n:], uint16(len(req.Region)))
	n += 2
	copy(buf[n:], req.Region)
	_, err := w.Write(buf)
	return err
}

func DecodeJoinRequest(data []byte) (JoinRequest, error) {
	// type byte already consumed by dispatchMessage
	if len(data) < 2 {
		return JoinRequest{}, ErrInvalidLength
	}
	pos := 0
	roomLen := int(binary.BigEndian.Uint16(data[pos:]))
	pos += 2
	if pos+roomLen > len(data) {
		return JoinRequest{}, ErrInvalidLength
	}
	roomCode := string(data[pos : pos+roomLen])
	pos += roomLen
	if pos+2 > len(data) {
		return JoinRequest{}, ErrInvalidLength
	}
	regionLen := int(binary.BigEndian.Uint16(data[pos:]))
	pos += 2
	if pos+regionLen > len(data) {
		return JoinRequest{}, ErrInvalidLength
	}
	region := string(data[pos : pos+regionLen])
	return JoinRequest{RoomCode: roomCode, Region: region}, nil
}

// --- JoinResponse ---

type ClientInfo struct {
	ClientID   byte
	PlayerName string
	PlayerID   byte
	IsMuted    bool
}

type JoinResponse struct {
	YourClientID byte
	Clients      []ClientInfo
	HostSettings []byte // raw host settings, or nil
}

func (m *JoinResponse) Type() byte { return TypeJoinResponse }

func EncodeJoinResponse(w io.Writer, resp JoinResponse) error {
	// Format: [type][1:your_id][2:client_count](for each: [1:id][2:name_len][name][1:pid][1:muted])[2:hs_len][hs]
	size := 1 + 1 + 2
	for _, c := range resp.Clients {
		size += 1 + 2 + len(c.PlayerName) + 1 + 1
	}
	hsLen := len(resp.HostSettings)
	size += 2 + hsLen

	buf := make([]byte, size)
	pos := 0
	buf[pos] = TypeJoinResponse
	pos++
	buf[pos] = resp.YourClientID
	pos++
	binary.BigEndian.PutUint16(buf[pos:], uint16(len(resp.Clients)))
	pos += 2
	for _, c := range resp.Clients {
		buf[pos] = c.ClientID
		pos++
		binary.BigEndian.PutUint16(buf[pos:], uint16(len(c.PlayerName)))
		pos += 2
		copy(buf[pos:], c.PlayerName)
		pos += len(c.PlayerName)
		buf[pos] = c.PlayerID
		pos++
		if c.IsMuted {
			buf[pos] = 1
		} else {
			buf[pos] = 0
		}
		pos++
	}
	binary.BigEndian.PutUint16(buf[pos:], uint16(hsLen))
	pos += 2
	if hsLen > 0 {
		copy(buf[pos:], resp.HostSettings)
	}
	_, err := w.Write(buf)
	return err
}

// --- Profile ---

type ProfileMsg struct {
	PlayerName string
	PlayerID   byte
}

func EncodeProfile(w io.Writer, m ProfileMsg) error {
	buf := make([]byte, 1+2+len(m.PlayerName)+1)
	pos := 0
	buf[pos] = TypeProfile
	pos++
	binary.BigEndian.PutUint16(buf[pos:], uint16(len(m.PlayerName)))
	pos += 2
	copy(buf[pos:], m.PlayerName)
	pos += len(m.PlayerName)
	buf[pos] = m.PlayerID
	_, err := w.Write(buf)
	return err
}

func DecodeProfile(data []byte) (ProfileMsg, error) {
	// type byte already consumed by dispatchMessage
	if len(data) < 2+1 {
		return ProfileMsg{}, ErrInvalidLength
	}
	pos := 0
	nameLen := int(binary.BigEndian.Uint16(data[pos:]))
	pos += 2
	if pos+nameLen+1 > len(data) {
		return ProfileMsg{}, ErrInvalidLength
	}
	name := string(data[pos : pos+nameLen])
	pos += nameLen
	pid := data[pos]
	return ProfileMsg{PlayerName: name, PlayerID: pid}, nil
}

// --- ProfileShare ---

type ProfileShareMsg struct {
	ClientID   byte
	PlayerName string
	PlayerID   byte
}

func EncodeProfileShare(w io.Writer, m ProfileShareMsg) error {
	buf := make([]byte, 1+1+2+len(m.PlayerName)+1)
	pos := 0
	buf[pos] = TypeProfileShare
	pos++
	buf[pos] = m.ClientID
	pos++
	binary.BigEndian.PutUint16(buf[pos:], uint16(len(m.PlayerName)))
	pos += 2
	copy(buf[pos:], m.PlayerName)
	pos += len(m.PlayerName)
	buf[pos] = m.PlayerID
	_, err := w.Write(buf)
	return err
}

// --- MuteStatus / MuteShare ---

type MuteStatusMsg struct {
	IsMuted         bool
	IsImpostorRadio bool
}

func EncodeMuteStatus(w io.Writer, m MuteStatusMsg) error {
	var flags byte
	if m.IsMuted {
		flags |= 1
	}
	if m.IsImpostorRadio {
		flags |= 2
	}
	_, err := w.Write([]byte{TypeMuteStatus, flags})
	return err
}

type MuteShareMsg struct {
	ClientID        byte
	IsMuted         bool
	IsImpostorRadio bool
}

func EncodeMuteShare(w io.Writer, m MuteShareMsg) error {
	var flags byte
	if m.IsMuted {
		flags |= 1
	}
	if m.IsImpostorRadio {
		flags |= 2
	}
	_, err := w.Write([]byte{TypeMuteShare, m.ClientID, flags})
	return err
}

// --- HostSettings ---

func EncodeHostSettings(w io.Writer, raw []byte) error {
	buf := make([]byte, 1+len(raw))
	buf[0] = TypeHostSettings
	copy(buf[1:], raw)
	_, err := w.Write(buf)
	return err
}

// --- ServerInfo ---

type ServerInfoMsg struct {
	OptimalPlayers int
	TotalClients   int
	ServerURL      string
}

func EncodeServerInfo(w io.Writer, m ServerInfoMsg) error {
	size := 1 + 2 + 2 + 2 + len(m.ServerURL)
	buf := make([]byte, size)
	pos := 0
	buf[pos] = TypeServerInfo
	pos++
	binary.BigEndian.PutUint16(buf[pos:], uint16(m.OptimalPlayers))
	pos += 2
	binary.BigEndian.PutUint16(buf[pos:], uint16(m.TotalClients))
	pos += 2
	binary.BigEndian.PutUint16(buf[pos:], uint16(len(m.ServerURL)))
	pos += 2
	copy(buf[pos:], m.ServerURL)
	_, err := w.Write(buf)
	return err
}

// --- AudioData ---
// Framing: [type=0x09][1:src_id][4:seq][2:duration_rtp][N:opus_data]

type AudioFrame struct {
	SourceID       byte
	SequenceNumber uint32
	DurationRTP    uint16
	OpusData       []byte
}

// EncodeAudioFrame writes a single audio frame to w.
func EncodeAudioFrame(w io.Writer, frame AudioFrame) error {
	buf := make([]byte, 1+1+4+2+len(frame.OpusData))
	pos := 0
	buf[pos] = TypeAudioData
	pos++
	buf[pos] = frame.SourceID
	pos++
	binary.BigEndian.PutUint32(buf[pos:], frame.SequenceNumber)
	pos += 4
	binary.BigEndian.PutUint16(buf[pos:], frame.DurationRTP)
	pos += 2
	copy(buf[pos:], frame.OpusData)
	_, err := w.Write(buf)
	return err
}

// DecodeAudioFrame decodes an audio frame. Type byte already consumed.
func DecodeAudioFrame(data []byte) (AudioFrame, int, error) {
	if len(data) < 7 {
		return AudioFrame{}, 0, ErrInvalidLength
	}
	pos := 0
	srcID := data[pos]
	pos++
	seq := binary.BigEndian.Uint32(data[pos:])
	pos += 4
	dur := binary.BigEndian.Uint16(data[pos:])
	pos += 2
	opusLen := len(data) - pos
	if opusLen <= 0 {
		return AudioFrame{}, 0, ErrInvalidLength
	}
	opusData := make([]byte, opusLen)
	copy(opusData, data[pos:])
	return AudioFrame{
		SourceID:       srcID,
		SequenceNumber: seq,
		DurationRTP:    dur,
		OpusData:       opusData,
	}, pos + opusLen, nil
}

// --- Leave ---

type LeaveMsg struct {
	ClientID byte
}

func EncodeLeave(w io.Writer, m LeaveMsg) error {
	_, err := w.Write([]byte{TypeLeave, m.ClientID})
	return err
}

// --- CustomData ---

func EncodeCustomData(w io.Writer, raw []byte) error {
	buf := make([]byte, 1+len(raw))
	buf[0] = TypeCustomData
	copy(buf[1:], raw)
	_, err := w.Write(buf)
	return err
}

// --- Ping/Pong ---

func EncodePing(w io.Writer) error {
	_, err := w.Write([]byte{TypePing})
	return err
}

func EncodePong(w io.Writer) error {
	_, err := w.Write([]byte{TypePong})
	return err
}

// --- Framed write helper ---
// Wraps one or more tagged messages in a length-prefixed frame suitable for
// a single WebSocket binary message.

// FrameWriter accumulates tagged messages and writes them as a single
// length-prefixed WebSocket binary frame.
type FrameWriter struct {
	buf []byte
}

// NewFrameWriter creates a FrameWriter with the given initial capacity.
func NewFrameWriter(capacity int) *FrameWriter {
	return &FrameWriter{buf: make([]byte, 2, capacity+2)}
}

// WriteTagged appends a pre-encoded tagged message.
func (fw *FrameWriter) WriteTagged(tagged []byte) {
	fw.buf = append(fw.buf, tagged...)
}

// Bytes returns the complete frame (with 2-byte length prefix).
func (fw *FrameWriter) Bytes() []byte {
	binary.BigEndian.PutUint16(fw.buf[:2], uint16(len(fw.buf)-2))
	return fw.buf
}

// Reset clears the writer for reuse.
func (fw *FrameWriter) Reset() {
	fw.buf = fw.buf[:2]
}

// FrameReader reads tagged messages from a length-prefixed frame.
// It returns a slice of raw tagged message payloads (including the type byte).
type FrameReader struct {
	data []byte
	pos  int
}

// NewFrameReader creates a FrameReader from a raw WebSocket binary message.
func NewFrameReader(data []byte) (*FrameReader, error) {
	if len(data) < 2 {
		return nil, ErrInvalidLength
	}
	payloadLen := int(binary.BigEndian.Uint16(data[:2]))
	if 2+payloadLen > len(data) {
		return nil, ErrInvalidLength
	}
	return &FrameReader{data: data[2 : 2+payloadLen], pos: 0}, nil
}

// Next returns the next tagged message. Returns nil when exhausted.
// The returned slice includes the type byte.
func (fr *FrameReader) Next() []byte {
	if fr.pos >= len(fr.data) {
		return nil
	}
	start := fr.pos
	msgType := fr.data[start]
	fr.pos++

	switch msgType {
	case TypeJoinRequest:
		return fr.readJoinRequest(start)
	case TypeJoinResponse:
		return fr.readJoinResponse(start)
	case TypeProfile:
		return fr.readProfile(start)
	case TypeMuteStatus:
		return fr.readFixed(start, 2)
	case TypeMuteShare:
		return fr.readFixed(start, 3)
	case TypeHostSettings:
		return fr.readHostSettings(start)
	case TypeAudioData:
		return fr.readAudioData(start)
	case TypeServerInfo:
		return fr.readServerInfo(start)
	case TypeLeave:
		return fr.readFixed(start, 2)
	case TypeCustomData:
		return fr.readCustomData(start)
	case TypePing, TypePong:
		return fr.data[start : start+1]
	default:
		// Unknown type: skip remaining bytes as fallback
		fr.pos = len(fr.data)
		return nil
	}
}

func (fr *FrameReader) readFixed(start, size int) []byte {
	end := start + size
	if end > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos = end
	return fr.data[start:end]
}

func (fr *FrameReader) readJoinRequest(start int) []byte {
	if fr.pos+2 > len(fr.data) {
		return nil
	}
	roomLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	if fr.pos+roomLen+2 > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos += roomLen
	regionLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	if fr.pos+regionLen > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos += regionLen
	return fr.data[start:fr.pos]
}

func (fr *FrameReader) readJoinResponse(start int) []byte {
	if fr.pos+3 > len(fr.data) {
		return nil
	}
	fr.pos++ // your client id
	clientCount := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	for i := 0; i < clientCount; i++ {
		if fr.pos+4 > len(fr.data) {
			fr.pos = len(fr.data)
			return nil
		}
		fr.pos++ // client id
		nameLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
		fr.pos += 2
		if fr.pos+nameLen+2 > len(fr.data) {
			fr.pos = len(fr.data)
			return nil
		}
		fr.pos += nameLen + 1 + 1 // name + pid + muted
	}
	if fr.pos+2 > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	hsLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	if fr.pos+hsLen > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos += hsLen
	return fr.data[start:fr.pos]
}

func (fr *FrameReader) readProfile(start int) []byte {
	if fr.pos+2 > len(fr.data) {
		return nil
	}
	nameLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	if fr.pos+nameLen+1 > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos += nameLen + 1
	return fr.data[start:fr.pos]
}

func (fr *FrameReader) readHostSettings(start int) []byte {
	end := len(fr.data)
	fr.pos = end
	return fr.data[start:end]
}

func (fr *FrameReader) readAudioData(start int) []byte {
	if fr.pos+6 > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	// Already consumed type byte; now src_id(1) + seq(4) + dur(2) + opus...
	fr.pos += 1 // src_id
	fr.pos += 4 // seq
	dur := binary.BigEndian.Uint16(fr.data[fr.pos-2:])
	_ = dur
	// Opus data is the rest of the frame (or until next message)
	// For simplicity, audio frames are sent one-per-WebSocket-message,
	// so the audio data extends to the end.
	fr.pos = len(fr.data)
	return fr.data[start:fr.pos]
}

func (fr *FrameReader) readServerInfo(start int) []byte {
	if fr.pos+6 > len(fr.data) {
		return nil
	}
	fr.pos += 4 // optimal_players(2) + total_clients(2)
	urlLen := int(binary.BigEndian.Uint16(fr.data[fr.pos:]))
	fr.pos += 2
	if fr.pos+urlLen > len(fr.data) {
		fr.pos = len(fr.data)
		return nil
	}
	fr.pos += urlLen
	return fr.data[start:fr.pos]
}

func (fr *FrameReader) readCustomData(start int) []byte {
	end := len(fr.data)
	fr.pos = end
	return fr.data[start:end]
}

// --- Helpers for writing tagged messages to bytes ---

func encodeTaggedHeader(typ byte, payloadLen int) []byte {
	buf := make([]byte, 1+payloadLen)
	buf[0] = typ
	return buf
}
