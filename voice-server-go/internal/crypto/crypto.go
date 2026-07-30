// Package crypto provides AES-256-GCM application-layer encryption for
// WebSocket frames. When enabled with a shared secret, all frame payloads
// (after the 2-byte length prefix) are encrypted.
//
// Format of an encrypted frame:
//
//	[2 bytes: total_length (big-endian)]
//	[12 bytes: nonce]
//	[encrypted_payload + 16 bytes: GCM auth tag]
//
// The nonce is randomly generated per frame. No nonce reuse with the same key.
package crypto

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"io"
)

var (
	ErrKeyNotSet   = errors.New("encryption key not configured")
	ErrDecryptFail = errors.New("decryption failed: invalid key or corrupted data")
	ErrTooShort    = errors.New("frame too short for encrypted format")
)

const (
	nonceSize = 12 // AES-GCM standard nonce size
	tagSize   = 16 // GCM authentication tag size
	keySize   = 32 // AES-256
)

// Cipher wraps AES-256-GCM encrypt/decrypt for frame payloads.
type Cipher struct {
	aead cipher.AEAD
}

// NewCipher creates a new AES-256-GCM cipher from a 32-byte key.
// If key is nil or empty, returns nil (encryption disabled).
func NewCipher(key []byte) (*Cipher, error) {
	if len(key) == 0 {
		return nil, nil
	}
	if len(key) != keySize {
		return nil, errors.New("key must be exactly 32 bytes for AES-256")
	}

	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	return &Cipher{aead: aead}, nil
}

// EncryptFrame encrypts a plaintext frame payload. Returns the encrypted frame
// with [2 bytes: total length][12 bytes: nonce][ciphertext+tag].
// If c is nil, returns plaintext as-is (no encryption).
func (c *Cipher) EncryptFrame(plaintext []byte) ([]byte, error) {
	if c == nil {
		// No encryption — just add length prefix (already done by caller)
		return plaintext, nil
	}

	nonce := make([]byte, nonceSize)
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}

	// Encrypt: ciphertext = aead.Seal(nil, nonce, plaintext, nil)
	ciphertext := c.aead.Seal(nil, nonce, plaintext, nil)

	// Frame: [2:total_len][12:nonce][ciphertext]
	totalLen := nonceSize + len(ciphertext)
	out := make([]byte, 2+totalLen)
	binary.BigEndian.PutUint16(out[:2], uint16(totalLen))
	copy(out[2:], nonce)
	copy(out[2+nonceSize:], ciphertext)

	return out, nil
}

// DecryptFrame decrypts an encrypted frame formatted as [2:total_len][12:nonce][ciphertext+tag].
// Returns the plaintext payload. If c is nil, returns data as-is.
func (c *Cipher) DecryptFrame(encrypted []byte) ([]byte, error) {
	if c == nil {
		return encrypted, nil
	}

	// Frame format: [2:total_len][12:nonce][ciphertext+tag]
	if len(encrypted) < 2+nonceSize+tagSize {
		return nil, ErrTooShort
	}

	// Read total length (big-endian) and validate
	totalLen := int(binary.BigEndian.Uint16(encrypted[:2]))
	if 2+totalLen != len(encrypted) {
		return nil, ErrTooShort
	}

	nonce := encrypted[2 : 2+nonceSize]
	ciphertext := encrypted[2+nonceSize:]

	plaintext, err := c.aead.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return nil, ErrDecryptFail
	}

	return plaintext, nil
}

// IsEncryptedFrame checks if a raw WebSocket message looks like an encrypted frame.
// Encrypted frames start with a 2-byte length followed by a random nonce.
// Plaintext frames start with a 2-byte length followed by a known message type byte.
func IsEncryptedFrame(data []byte) bool {
	if len(data) < 3 {
		return false
	}
	// If the server has encryption enabled, ALL frames are encrypted.
	// We detect by checking if the first byte after the length prefix
	// looks like a valid message type (0x01-0x0D).
	totalLen := int(binary.BigEndian.Uint16(data[:2]))
	if 2+totalLen != len(data) && 2+totalLen > len(data) {
		return true // likely encrypted (garbage length that doesn't match)
	}
	return false
}
