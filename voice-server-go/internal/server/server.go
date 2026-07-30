package server

import (
	"fmt"
	"log"
	"net/http"
	"sync"
	"time"

	"github.com/gorilla/websocket"

	"github.com/interstellar/voice-server/internal/crypto"
	"github.com/interstellar/voice-server/internal/protocol"
)

// Config holds the server configuration.
type Config struct {
	ListenAddr     string // e.g., ":8000"
	OptimalPlayers int
	TurnURL        string
	TurnUser       string
	TurnPass       string
	CertFile       string // TLS cert (optional)
	KeyFile        string // TLS key (optional)

	// AES-256-GCM encryption (optional, 32-byte key)
	SecretKey []byte

	// Audio redundancy for loss mitigation (0=off, 1=2x, 2=3x)
	Redundancy int

	// Max bandwidth per client in bytes/sec (0=unlimited, default ~32KB/s)
	MaxBandwidthPerClient int64
}

// Server is the main voice chat server.
type Server struct {
	config     Config
	upgrader   websocket.Upgrader
	rooms      map[string]*Room
	roomsMu    sync.RWMutex
	startTime  time.Time
	totalConns int64 // atomic — total connections ever made
	cipher     *crypto.Cipher
}

// New creates a new Server.
func New(cfg Config) *Server {
	ciph, err := crypto.NewCipher(cfg.SecretKey)
	if err != nil {
		log.Printf("WARNING: encryption setup failed: %v (running without encryption)", err)
		ciph = nil
	}
	if ciph != nil {
		log.Printf("AES-256-GCM encryption enabled")
	}
	if cfg.Redundancy > 0 {
		log.Printf("Audio redundancy: %dx (sending each frame %d times)", cfg.Redundancy+1, cfg.Redundancy+1)
	}
	if cfg.MaxBandwidthPerClient > 0 {
		log.Printf("Per-client bandwidth limit: %d bytes/sec", cfg.MaxBandwidthPerClient)
	}

	return &Server{
		config:    cfg,
		cipher:    ciph,
		rooms:     make(map[string]*Room),
		startTime: time.Now(),
		upgrader: websocket.Upgrader{
			ReadBufferSize:  65536,
			WriteBufferSize: 65536,
			CheckOrigin: func(r *http.Request) bool {
				return true // allow all origins for game client
			},
			EnableCompression: true,
		},
	}
}

// Start begins listening and serving.
func (s *Server) Start() error {
	http.HandleFunc("/vc", s.handleWebSocket)
	http.HandleFunc("/health", s.handleHealth)
	http.HandleFunc("/", s.handleDashboard)

	addr := s.config.ListenAddr
	log.Printf("Interstellar Voice Server (Go) starting on %s", addr)
	log.Printf("  WebSocket: ws://%s/vc", addr)
	if s.config.CertFile != "" {
		log.Printf("  Secure:    wss://%s/vc", addr)
	}
	if s.config.TurnURL != "" {
		log.Printf("  TURN:      %s", s.config.TurnURL)
	}
	if s.config.OptimalPlayers > 0 {
		log.Printf("  Optimal:   %d players", s.config.OptimalPlayers)
	}
	if s.cipher != nil {
		log.Printf("  Encrypted: AES-256-GCM (E2E)")
	}
	if s.config.Redundancy > 0 {
		log.Printf("  Redundancy: %dx", s.config.Redundancy+1)
	}
	log.Printf("  Dashboard: http://%s/", addr)

	if s.config.CertFile != "" && s.config.KeyFile != "" {
		return http.ListenAndServeTLS(addr, s.config.CertFile, s.config.KeyFile, nil)
	}
	return http.ListenAndServe(addr, nil)
}

// handleWebSocket handles WebSocket upgrade requests.
func (s *Server) handleWebSocket(w http.ResponseWriter, r *http.Request) {
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("websocket upgrade error: %v", err)
		return
	}

	client := newClient(conn, s)

	// Start goroutines
	go client.readLoop()
	go client.runAudioSender()
	go client.runPinger()

	log.Printf("client connected from %s", r.RemoteAddr)
}

// handleJoin processes a join request.
func (s *Server) handleJoin(c *Client, req protocol.JoinRequest) {
	roomKey := req.Region + "." + req.RoomCode

	s.roomsMu.Lock()
	room, exists := s.rooms[roomKey]
	if !exists {
		room = newRoom(roomKey)
		room.server = s // set back-reference
		s.rooms[roomKey] = room
	}
	s.roomsMu.Unlock()

	if !room.addClient(c) {
		log.Printf("room %s is full, rejecting client", roomKey)
		c.Close()
		return
	}

	// Send join response with existing clients
	existingClients := room.getExistingClientInfos(c)
	hostSettings := room.getHostSettings()

	if err := c.SendJoinResponse(existingClients, hostSettings); err != nil {
		log.Printf("client %d: send join response error: %v", c.clientID, err)
		c.Close()
		return
	}

	// Send server info
	c.SendServerInfo(protocol.ServerInfoMsg{
		OptimalPlayers: s.config.OptimalPlayers,
		TotalClients:   s.RoomCount(),
		ServerURL:      fmt.Sprintf("ws://%s/vc", s.config.ListenAddr),
	})

	log.Printf("room %s: client %d assigned (total: %d)", roomKey, c.clientID, room.clientCount())
}

// removeRoom removes a room from the server.
func (s *Server) removeRoom(key string) {
	s.roomsMu.Lock()
	defer s.roomsMu.Unlock()
	delete(s.rooms, key)
	log.Printf("room %s removed (empty)", key)
}

// RoomCount returns the total number of active rooms.
func (s *Server) RoomCount() int {
	s.roomsMu.RLock()
	defer s.roomsMu.RUnlock()
	return len(s.rooms)
}

// TotalClients returns the total number of connected clients.
func (s *Server) TotalClients() int {
	s.roomsMu.RLock()
	defer s.roomsMu.RUnlock()
	count := 0
	for _, room := range s.rooms {
		count += room.clientCount()
	}
	return count
}

// GetSnapshots returns snapshots of all rooms.
func (s *Server) GetSnapshots() []RoomSnapshot {
	s.roomsMu.RLock()
	defer s.roomsMu.RUnlock()

	snapshots := make([]RoomSnapshot, 0, len(s.rooms))
	for _, room := range s.rooms {
		snapshots = append(snapshots, room.snapshot())
	}
	return snapshots
}

// Uptime returns how long the server has been running.
func (s *Server) Uptime() time.Duration {
	return time.Since(s.startTime)
}

// Config returns the server config (read-only).
func (s *Server) Config() Config {
	return s.config
}
