// Package protocol defines the binary message protocol for the Interstellar Voice server.
//
// All messages are WebSocket binary frames with the following framing:
//
//	[2 bytes: payload_length (big-endian, excluding these 2 bytes)]
//	[payload_length bytes: one or more tagged messages]
//
// Each tagged message within the payload:
//
//	[1 byte: message_type]
//	[varies: type-specific payload]
//
// Audio frames have their own dedicated framing for minimal overhead:
//
//	[1 byte: type = 0x09]
//	[1 byte: source_client_id]
//	[4 bytes: sequence_number (big-endian)]
//	[2 bytes: duration_rtp_units (big-endian)]
//	[N bytes: opus_encoded_audio]
package protocol

// Message types for signaling and control.
const (
	TypeJoinRequest  byte = 0x01 // Client → Server: join a room
	TypeJoinResponse byte = 0x02 // Server → Client: assigned client ID + existing clients
	TypeLeave        byte = 0x03 // Client → Server / Server → Client: client left
	TypeProfile      byte = 0x04 // Client → Server: update own profile
	TypeProfileShare byte = 0x05 // Server → Client: another client's profile
	TypeMuteStatus   byte = 0x06 // Client → Server: update own mute status
	TypeMuteShare    byte = 0x07 // Server → Client: another client's mute status
	TypeHostSettings byte = 0x08 // Client → Server / Server → Client: host room settings
	TypeAudioData    byte = 0x09 // Client → Server / Server → Client: Opus audio frame
	TypeServerInfo   byte = 0x0A // Server → Client: server info (player count, URL, etc.)
	TypeCustomData   byte = 0x0B // Bidirectional: custom opaque data relay
	TypePing         byte = 0x0C // Bidirectional: latency measurement
	TypePong         byte = 0x0D // Bidirectional: latency measurement reply
)

// MaxClientsPerRoom is the maximum number of clients allowed in a single room.
const MaxClientsPerRoom = 63

// MaxAudioFrameSize is the maximum size of an Opus-encoded audio frame payload.
const MaxAudioFrameSize = 4096

// AudioClockRate is the Opus sample rate used throughout the system.
const AudioClockRate = 48000
