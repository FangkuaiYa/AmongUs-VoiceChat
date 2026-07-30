// Interstellar Voice Server (Go Edition)
//
// A lightweight, high-performance voice chat relay server for Among Us.
// Replaces the original C# server with minimal resource usage and no GC pauses
// that interfere with the Among Us game server.
//
// Usage:
//
//	go run . [flags]
//
// Flags:
//
//	-addr string       Listen address (default ":8000")
//	-optimal int       Optimal player count hint
//	-turn string       TURN server URL
//	-turn-user string  TURN server username
//	-turn-pass string  TURN server password
//	-tls-cert string   TLS certificate path (enables WSS)
//	-tls-key string    TLS key path (enables WSS)
package main

import (
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/interstellar/voice-server/internal/server"
)

func main() {
	addr := flag.String("addr", ":8000", "Listen address (e.g., :8000 or 0.0.0.0:8000)")
	optimal := flag.Int("optimal", 0, "Optimal player count hint")
	turnURL := flag.String("turn", "", "TURN server URL (e.g., turn:example.com:3478)")
	turnUser := flag.String("turn-user", "", "TURN server username")
	turnPass := flag.String("turn-pass", "", "TURN server password")
	tlsCert := flag.String("tls-cert", "", "TLS certificate file path (enables WSS)")
	tlsKey := flag.String("tls-key", "", "TLS key file path (enables WSS)")
	secretKey := flag.String("secret", "", "AES-256-GCM encryption key (32 bytes as hex string, e.g., 'a1b2...' — 64 hex chars)")
	redundancy := flag.Int("redundancy", 0, "Audio redundancy for loss mitigation (0=off, 1=2x each frame, 2=3x)")
	maxBW := flag.Int64("max-bandwidth", 0, "Max bandwidth per client in bytes/sec (0=unlimited, default ~32KB/s recommended)")
	flag.Parse()

	// Also read from env vars for Docker compatibility
	if *optimal <= 0 {
		if env := os.Getenv("OPTIMAL_PLAYERS"); env != "" {
			if v, err := parseInt(env); err == nil {
				*optimal = v
			}
		}
	}
	if *turnURL == "" {
		*turnURL = os.Getenv("TURN_URL")
	}
	if *turnUser == "" {
		*turnUser = os.Getenv("TURN_USER")
	}
	if *turnPass == "" {
		*turnPass = os.Getenv("TURN_PASS")
	}
	if *tlsCert == "" {
		*tlsCert = os.Getenv("TLS_CERT")
	}
	if *tlsKey == "" {
		*tlsKey = os.Getenv("TLS_KEY")
	}

	// Parse secret key from hex
	var keyBytes []byte
	if *secretKey != "" {
		keyBytes = parseHexKey(*secretKey)
	}

	// Default bandwidth if not set: 32KB/s per client
	bwLimit := *maxBW
	if bwLimit == 0 {
		if env := os.Getenv("MAX_BANDWIDTH_PER_CLIENT"); env != "" {
			if v, err := parseInt64(env); err == nil {
				bwLimit = v
			}
		}
	}

	cfg := server.Config{
		ListenAddr:            *addr,
		OptimalPlayers:        *optimal,
		TurnURL:               *turnURL,
		TurnUser:              *turnUser,
		TurnPass:              *turnPass,
		CertFile:              *tlsCert,
		KeyFile:               *tlsKey,
		SecretKey:             keyBytes,
		Redundancy:            *redundancy,
		MaxBandwidthPerClient: bwLimit,
	}

	srv := server.New(cfg)

	// Graceful shutdown
	go func() {
		sigCh := make(chan os.Signal, 1)
		signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
		<-sigCh
		log.Println("Shutting down...")
		os.Exit(0)
	}()

	if err := srv.Start(); err != nil {
		log.Fatalf("Server error: %v", err)
	}
}

func parseInt(s string) (int, error) {
	var n int
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("invalid number: %s", s)
		}
		n = n*10 + int(c-'0')
	}
	return n, nil
}

func parseInt64(s string) (int64, error) {
	var n int64
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("invalid number: %s", s)
		}
		n = n*10 + int64(c-'0')
	}
	return n, nil
}

// parseHexKey parses a 64-character hex string into a 32-byte key.
func parseHexKey(hex string) []byte {
	if len(hex) != 64 {
		log.Printf("WARNING: secret key must be exactly 64 hex characters (32 bytes), got %d — encryption disabled", len(hex))
		return nil
	}
	key := make([]byte, 32)
	for i := 0; i < 32; i++ {
		var b byte
		if _, err := fmt.Sscanf(hex[i*2:i*2+2], "%02x", &b); err != nil {
			log.Printf("WARNING: invalid hex in secret key at position %d — encryption disabled", i*2)
			return nil
		}
		key[i] = b
	}
	return key
}
