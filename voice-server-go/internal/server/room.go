package server

import (
	"log"
	"sync"

	"github.com/interstellar/voice-server/internal/protocol"
)

// Room represents a voice chat room containing multiple clients.
type Room struct {
	key    string // "region.roomCode"
	server *Server // back-reference for cleanup

	mu      sync.RWMutex
	clients map[byte]*Client

	// Last host settings broadcast (cached for new joins)
	hostSettings   []byte
	hostSettingsMu sync.RWMutex

	logger *log.Logger
}

// newRoom creates a new Room.
func newRoom(key string) *Room {
	return &Room{
		key:     key,
		clients: make(map[byte]*Client),
		logger:  log.Default(),
	}
}

// availableID returns the lowest available client ID.
func (r *Room) availableID() byte {
	r.mu.RLock()
	defer r.mu.RUnlock()
	var id byte
	for {
		if _, exists := r.clients[id]; !exists {
			return id
		}
		id++
		if id >= protocol.MaxClientsPerRoom {
			return protocol.MaxClientsPerRoom // will be rejected
		}
	}
}

// addClient adds a client to the room. Returns false if room is full.
func (r *Room) addClient(c *Client) bool {
	r.mu.Lock()
	defer r.mu.Unlock()

	if len(r.clients) >= protocol.MaxClientsPerRoom {
		return false
	}

	id := r.availableID()
	if id >= protocol.MaxClientsPerRoom {
		return false
	}

	c.clientID = id
	c.room = r
	r.clients[id] = c

	r.logger.Printf("room %s: client %d joined (total: %d)", r.key, id, len(r.clients))
	return true
}

// removeClient removes a client from the room.
func (r *Room) removeClient(c *Client) {
	r.mu.Lock()
	clientID := c.clientID
	delete(r.clients, clientID)
	remaining := len(r.clients)
	r.mu.Unlock()

	c.room = nil

	// Notify remaining clients
	r.mu.RLock()
	clients := make([]*Client, 0, len(r.clients))
	for _, cl := range r.clients {
		clients = append(clients, cl)
	}
	r.mu.RUnlock()

	for _, cl := range clients {
		cl.SendClientLeft(clientID)
	}

	r.logger.Printf("room %s: client %d left (total: %d)", r.key, clientID, remaining)

	// Clean up empty room
	if remaining == 0 {
		r.server.removeRoom(r.key)
	}
}

// getExistingClientInfos returns info for all clients except the given one.
func (r *Room) getExistingClientInfos(exclude *Client) []protocol.ClientInfo {
	r.mu.RLock()
	defer r.mu.RUnlock()

	infos := make([]protocol.ClientInfo, 0, len(r.clients))
	for _, c := range r.clients {
		if c.clientID == exclude.clientID {
			continue
		}
		infos = append(infos, protocol.ClientInfo{
			ClientID:   c.clientID,
			PlayerName: c.playerName,
			PlayerID:   c.playerID,
			IsMuted:    c.isMuted.Load(),
		})
	}
	return infos
}

// broadcastAudio relays an audio frame to all other clients with optional redundancy.
func (r *Room) broadcastAudio(sender *Client, frame *protocol.AudioFrame) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	redundancy := r.server.config.Redundancy

	for _, c := range r.clients {
		if c.clientID == sender.clientID {
			continue
		}

		// Enqueue the primary frame
		c.EnqueueAudio(frame)

		// Send redundant copies for loss mitigation
		for i := 0; i < redundancy; i++ {
			dup := *frame // shallow copy
			dup.OpusData = make([]byte, len(frame.OpusData))
			copy(dup.OpusData, frame.OpusData)
			c.EnqueueAudio(&dup)
		}
	}
}

// broadcastProfile sends a profile update to all other clients.
func (r *Room) broadcastProfile(sender *Client, name string, pid byte) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	for _, c := range r.clients {
		if c.clientID == sender.clientID {
			continue
		}
		c.SendProfileShare(sender.clientID, name, pid)
	}
}

// broadcastMuteStatus sends a mute status update to all other clients.
func (r *Room) broadcastMuteStatus(sender *Client, muted, impostorRadio bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	for _, c := range r.clients {
		if c.clientID == sender.clientID {
			continue
		}
		c.SendMuteShare(sender.clientID, muted, impostorRadio)
	}
}

// setHostSettings caches host settings for new joins.
func (r *Room) setHostSettings(raw []byte) {
	r.hostSettingsMu.Lock()
	defer r.hostSettingsMu.Unlock()
	r.hostSettings = make([]byte, len(raw))
	copy(r.hostSettings, raw)
}

// getHostSettings returns the cached host settings.
func (r *Room) getHostSettings() []byte {
	r.hostSettingsMu.RLock()
	defer r.hostSettingsMu.RUnlock()
	if r.hostSettings == nil {
		return nil
	}
	hs := make([]byte, len(r.hostSettings))
	copy(hs, r.hostSettings)
	return hs
}

// broadcastHostSettings sends host settings to all other clients.
func (r *Room) broadcastHostSettings(sender *Client, raw []byte) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	for _, c := range r.clients {
		if c.clientID == sender.clientID {
			continue
		}
		c.SendHostSettings(raw)
	}
}

// broadcastCustomData relays custom data to all other clients.
func (r *Room) broadcastCustomData(sender *Client, raw []byte) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	for _, c := range r.clients {
		if c.clientID == sender.clientID {
			continue
		}
		c.SendCustomData(raw)
	}
}

// clientCount returns the number of clients in the room.
func (r *Room) clientCount() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.clients)
}

// snapshot returns a snapshot of room state for the dashboard.
func (r *Room) snapshot() RoomSnapshot {
	r.mu.RLock()
	defer r.mu.RUnlock()

	clients := make([]ClientSnapshot, 0, len(r.clients))
	for _, c := range r.clients {
		clients = append(clients, ClientSnapshot{
			ClientID:   c.clientID,
			PlayerName: c.playerName,
			PlayerID:   c.playerID,
			IsMuted:    c.isMuted.Load(),
		})
	}
	return RoomSnapshot{
		Key:     r.key,
		Clients: clients,
	}
}

// RoomSnapshot is used for dashboard display.
type RoomSnapshot struct {
	Key     string           `json:"key"`
	Clients []ClientSnapshot `json:"clients"`
}

// ClientSnapshot is used for dashboard display.
type ClientSnapshot struct {
	ClientID   byte   `json:"clientId"`
	PlayerName string `json:"playerName"`
	PlayerID   byte   `json:"playerId"`
	IsMuted    bool   `json:"isMuted"`
}
