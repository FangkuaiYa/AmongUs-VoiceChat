package audio

import (
	"container/heap"
	"sync"
	"time"
)

// JitterBuffer reorders incoming audio frames by sequence number and
// releases them in order after a configurable delay.
type JitterBuffer struct {
	mu       sync.Mutex
	heap     frameHeap
	nextSeq  uint32
	init     bool
	maxDelay int // max frames to buffer before forced flush
	lastFlush time.Time
}

// NewJitterBuffer creates a jitter buffer with the given max delay in frames.
// Typical values: 3-5 frames (60-100ms at 20ms frames).
func NewJitterBuffer(maxFrames int) *JitterBuffer {
	return &JitterBuffer{
		maxDelay: maxFrames,
		heap:     make(frameHeap, 0, maxFrames*2),
	}
}

// Push adds a frame to the buffer. Returns ordered frames ready for output.
func (jb *JitterBuffer) Push(seq uint32, data []byte) [][]byte {
	jb.mu.Lock()
	defer jb.mu.Unlock()

	if !jb.init {
		jb.nextSeq = seq
		jb.init = true
		jb.lastFlush = time.Now()
		return [][]byte{data}
	}

	// If this is the expected next frame, output immediately
	if seq == jb.nextSeq {
		jb.nextSeq++
		var out [][]byte
		out = append(out, data)

		// Drain any buffered frames that are now in sequence
		for len(jb.heap) > 0 && jb.heap[0].seq == jb.nextSeq {
			f := heap.Pop(&jb.heap).(frameEntry)
			out = append(out, f.data)
			jb.nextSeq++
		}
		return out
	}

	// Out of order — buffer it
	heap.Push(&jb.heap, frameEntry{seq: seq, data: data, arrived: time.Now()})

	// Check for forced flush (buffer too full or too old)
	if len(jb.heap) > jb.maxDelay || time.Since(jb.lastFlush) > 100*time.Millisecond {
		return jb.forceFlush()
	}

	return nil
}

// forceFlush drains all buffered frames in order, skipping gaps.
func (jb *JitterBuffer) forceFlush() [][]byte {
	jb.lastFlush = time.Now()
	if len(jb.heap) == 0 {
		return nil
	}

	var out [][]byte
	for len(jb.heap) > 0 {
		f := heap.Pop(&jb.heap).(frameEntry)
		if f.seq >= jb.nextSeq {
			jb.nextSeq = f.seq + 1
		}
		out = append(out, f.data)
	}
	return out
}

// Flush drains all buffered frames.
func (jb *JitterBuffer) Flush() [][]byte {
	jb.mu.Lock()
	defer jb.mu.Unlock()
	return jb.forceFlush()
}

// Reset clears the buffer (e.g., on client disconnect).
func (jb *JitterBuffer) Reset() {
	jb.mu.Lock()
	defer jb.mu.Unlock()
	jb.heap = jb.heap[:0]
	jb.init = false
	jb.nextSeq = 0
}

// ── Min-heap for frame ordering ──────────────────────────────────

type frameEntry struct {
	seq     uint32
	data    []byte
	arrived time.Time
}

type frameHeap []frameEntry

func (h frameHeap) Len() int           { return len(h) }
func (h frameHeap) Less(i, j int) bool { return h[i].seq < h[j].seq }
func (h frameHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }

func (h *frameHeap) Push(x any) {
	*h = append(*h, x.(frameEntry))
}

func (h *frameHeap) Pop() any {
	old := *h
	n := len(old)
	x := old[n-1]
	*h = old[0 : n-1]
	return x
}

// ── Redundancy / FEC ─────────────────────────────────────────────

// RedundancyConfig controls audio frame redundancy for loss mitigation.
type RedundancyConfig struct {
	// DuplicateCount is how many extra copies of each frame to send.
	// 0 = no redundancy, 1 = send each frame twice, etc.
	DuplicateCount int
}

// DefaultRedundancy returns a config with no redundancy.
func DefaultRedundancy() RedundancyConfig {
	return RedundancyConfig{DuplicateCount: 0}
}

// ── Bandwidth Limiter ────────────────────────────────────────────

// RateLimiter implements a simple token bucket for bandwidth control.
type RateLimiter struct {
	mu        sync.Mutex
	rate      int64 // bytes per second
	burst     int64 // max burst bytes
	tokens    float64
	lastCheck time.Time
}

// NewRateLimiter creates a token bucket rate limiter.
// rate: bytes per second, burst: max burst bytes.
func NewRateLimiter(rate int64, burst int64) *RateLimiter {
	return &RateLimiter{
		rate:      rate,
		burst:     burst,
		tokens:    float64(burst),
		lastCheck: time.Now(),
	}
}

// Allow checks if n bytes are allowed. Returns true if allowed.
func (rl *RateLimiter) Allow(n int) bool {
	rl.mu.Lock()
	defer rl.mu.Unlock()

	now := time.Now()
	elapsed := now.Sub(rl.lastCheck).Seconds()
	rl.tokens += elapsed * float64(rl.rate)
	if rl.tokens > float64(rl.burst) {
		rl.tokens = float64(rl.burst)
	}
	rl.lastCheck = now

	if rl.tokens >= float64(n) {
		rl.tokens -= float64(n)
		return true
	}
	return false
}
