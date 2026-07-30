// Package audio provides audio relay, mixing, and jitter buffering.
package audio

import (
	"sync"
	"sync/atomic"
	"time"
)

// RelayMode determines how audio is forwarded.
type RelayMode int

const (
	// RelayPerSource sends each source's audio as a separate frame.
	// Clients receive one audio frame per speaking peer.
	RelayPerSource RelayMode = iota

	// RelayMixed mixes all sources into a single audio frame per listener.
	// Reduces bandwidth for clients but increases server CPU.
	// NOTE: Mixing requires Opus decoding/re-encoding on the server.
	// Currently not implemented — reserved for future.
	RelayMixed
)

// Mixer handles optional server-side audio mixing.
type Mixer struct {
	mu      sync.Mutex
	buffers map[byte]*mixBuffer
}

type mixBuffer struct {
	frames   [][]byte
	lastSeq  uint32
	lastTime time.Time
}

// NewMixer creates a new audio mixer.
func NewMixer() *Mixer {
	return &Mixer{
		buffers: make(map[byte]*mixBuffer),
	}
}

// AddFrame adds an audio frame for a source. When enough frames accumulate,
// it returns the mixed PCM samples (not yet implemented).
func (m *Mixer) AddFrame(sourceID byte, opusData []byte, seq uint32) []byte {
	// Mixing placeholder — for now just returns nil.
	// Opus mixing is complex and typically done via:
	// 1. Decode each source to PCM
	// 2. Sum PCM samples (with overflow protection)
	// 3. Re-encode with Opus
	_ = sourceID
	_ = opusData
	_ = seq
	return nil
}

// Cleanup removes stale buffers.
func (m *Mixer) Cleanup(maxAge time.Duration) {
	m.mu.Lock()
	defer m.mu.Unlock()
	now := time.Now()
	for id, buf := range m.buffers {
		if now.Sub(buf.lastTime) > maxAge {
			delete(m.buffers, id)
		}
	}
}

// --- Sequence tracking for packet loss detection (informational) ---

// SeqTracker tracks per-source sequence numbers.
type SeqTracker struct {
	mu      sync.Mutex
	tracked map[byte]*seqState
}

type seqState struct {
	lastSeq    uint32
	packets    uint64
	lost       uint64
	initialized bool
}

// NewSeqTracker creates a new sequence tracker.
func NewSeqTracker() *SeqTracker {
	return &SeqTracker{tracked: make(map[byte]*seqState)}
}

// Record records a sequence number for a source. Returns true if a gap was detected.
func (st *SeqTracker) Record(sourceID byte, seq uint32) (gap bool) {
	st.mu.Lock()
	defer st.mu.Unlock()

	s, ok := st.tracked[sourceID]
	if !ok {
		s = &seqState{lastSeq: seq, initialized: true, packets: 1}
		st.tracked[sourceID] = s
		return false
	}

	s.packets++
	if !s.initialized {
		s.lastSeq = seq
		s.initialized = true
		return false
	}

	expected := s.lastSeq + 1
	if seq > expected {
		s.lost += uint64(seq - expected)
		s.lastSeq = seq
		return true
	}
	s.lastSeq = seq
	return false
}

// Stats returns packet and loss counts for a source.
func (st *SeqTracker) Stats(sourceID byte) (packets, lost uint64) {
	st.mu.Lock()
	defer st.mu.Unlock()
	if s, ok := st.tracked[sourceID]; ok {
		return s.packets, s.lost
	}
	return 0, 0
}

// LossRate returns the loss rate (0.0–1.0) for a source.
func (st *SeqTracker) LossRate(sourceID byte) float64 {
	packets, lost := st.Stats(sourceID)
	if packets == 0 {
		return 0
	}
	return float64(lost) / float64(packets+lost)
}

// Remove cleans up tracking for a source.
func (st *SeqTracker) Remove(sourceID byte) {
	st.mu.Lock()
	defer st.mu.Unlock()
	delete(st.tracked, sourceID)
}

// --- Throughput tracking ---

// Throughput tracks bytes-per-second for a connection.
type Throughput struct {
	bytes     atomic.Uint64
	lastBytes uint64
	lastTime  time.Time
	bps       atomic.Uint64
}

// Add records n bytes transferred.
func (t *Throughput) Add(n int) {
	t.bytes.Add(uint64(n))
}

// BPS returns the current bytes-per-second rate.
func (t *Throughput) BPS() uint64 {
	now := time.Now()
	lastTime := t.lastTime
	if lastTime.IsZero() {
		t.lastTime = now
		t.lastBytes = t.bytes.Load()
		return 0
	}
	elapsed := now.Sub(lastTime)
	if elapsed >= time.Second {
		current := t.bytes.Load()
		rate := uint64(float64(current-t.lastBytes) / elapsed.Seconds())
		t.bps.Store(rate)
		t.lastBytes = current
		t.lastTime = now
	}
	return t.bps.Load()
}

// TotalBytes returns total bytes transferred.
func (t *Throughput) TotalBytes() uint64 {
	return t.bytes.Load()
}
