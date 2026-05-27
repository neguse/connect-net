package main

import (
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
)

func main() {
	mux := http.NewServeMux()

	mux.HandleFunc("/example.GreeterService/SayHello", handleSayHello)
	mux.HandleFunc("/example.GreeterService/SayHelloStream", handleSayHelloStream)
	mux.HandleFunc("/example.GreeterService/CollectHellos", handleCollectHellos)
	mux.HandleFunc("/example.GreeterService/Chat", handleChat)

	port := "18080"
	if p := os.Getenv("PORT"); p != "" {
		port = p
	}
	fmt.Fprintf(os.Stderr, "testserver listening on :%s\n", port)
	if err := http.ListenAndServe(":"+port, mux); err != nil {
		fmt.Fprintf(os.Stderr, "server error: %v\n", err)
		os.Exit(1)
	}
}

// --- Manual protobuf encoding/decoding for field 1 (string) ---

func encodeStringField(s string) []byte {
	sBytes := []byte(s)
	lenBytes := encodeVarint(uint64(len(sBytes)))
	b := make([]byte, 0, 1+len(lenBytes)+len(sBytes))
	b = append(b, 0x0a) // field 1, wire type 2 (length-delimited)
	b = append(b, lenBytes...)
	b = append(b, sBytes...)
	return b
}

func decodeStringField(data []byte) string {
	if len(data) == 0 {
		return ""
	}
	idx := 0
	for idx < len(data) {
		if idx >= len(data) {
			return ""
		}
		tag := data[idx]
		idx++
		fieldNum := tag >> 3
		wireType := tag & 0x07

		if wireType == 2 { // length-delimited
			length, bytesRead := decodeVarint(data[idx:])
			idx += bytesRead
			if fieldNum == 1 {
				end := idx + int(length)
				if end > len(data) {
					return ""
				}
				return string(data[idx:end])
			}
			idx += int(length)
		} else if wireType == 0 { // varint
			_, bytesRead := decodeVarint(data[idx:])
			idx += bytesRead
		} else {
			return ""
		}
	}
	return ""
}

func encodeVarint(v uint64) []byte {
	var buf [10]byte
	n := 0
	for v >= 0x80 {
		buf[n] = byte(v) | 0x80
		v >>= 7
		n++
	}
	buf[n] = byte(v)
	n++
	return buf[:n]
}

func decodeVarint(data []byte) (uint64, int) {
	var val uint64
	for i, b := range data {
		val |= uint64(b&0x7f) << (7 * uint(i))
		if b&0x80 == 0 {
			return val, i + 1
		}
		if i >= 9 {
			break
		}
	}
	return val, len(data)
}

// --- Connect protocol helpers ---

func writeConnectError(w http.ResponseWriter, code string, msg string, httpStatus int) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(httpStatus)
	json.NewEncoder(w).Encode(map[string]interface{}{
		"code":    code,
		"message": msg,
	})
}

func writeStreamEnvelope(w http.ResponseWriter, flags byte, data []byte) {
	header := make([]byte, 5)
	header[0] = flags
	binary.BigEndian.PutUint32(header[1:5], uint32(len(data)))
	w.Write(header)
	w.Write(data)
}

func readStreamEnvelope(r io.Reader) (flags byte, data []byte, err error) {
	header := make([]byte, 5)
	if _, err = io.ReadFull(r, header); err != nil {
		return 0, nil, err
	}
	flags = header[0]
	length := binary.BigEndian.Uint32(header[1:5])
	data = make([]byte, length)
	if _, err = io.ReadFull(r, data); err != nil {
		return 0, nil, err
	}
	return flags, data, nil
}

func writeEndStream(w http.ResponseWriter) {
	endStream := []byte(`{}`)
	writeStreamEnvelope(w, 0x02, endStream)
}

func writeStreamError(w http.ResponseWriter, code string, msg string) {
	errJSON, _ := json.Marshal(map[string]interface{}{
		"error": map[string]interface{}{
			"code":    code,
			"message": msg,
		},
	})
	writeStreamEnvelope(w, 0x02, errJSON)
}

// --- Handlers ---

func handleSayHello(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet {
		handleSayHelloGet(w, r)
		return
	}

	ct := r.Header.Get("Content-Type")

	body, err := io.ReadAll(r.Body)
	if err != nil {
		writeConnectError(w, "internal", "failed to read body", 500)
		return
	}

	var name string
	if strings.HasPrefix(ct, "application/json") {
		var req map[string]string
		json.Unmarshal(body, &req)
		name = req["name"]
	} else {
		name = decodeStringField(body)
	}

	if name == "" {
		writeConnectError(w, "invalid_argument", "name is required", 400)
		return
	}

	message := fmt.Sprintf("Hello %s from connect-go", name)

	if strings.HasPrefix(ct, "application/json") {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]string{"message": message})
	} else {
		w.Header().Set("Content-Type", "application/proto")
		w.Write(encodeStringField(message))
	}
}

func handleSayHelloGet(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	encoding := q.Get("encoding")
	msgBase64 := q.Get("message")
	connectVersion := q.Get("connect")

	if connectVersion != "v1" {
		writeConnectError(w, "invalid_argument", "missing connect version", 400)
		return
	}

	// Decode base64url message
	// Add padding if needed
	padded := msgBase64
	switch len(padded) % 4 {
	case 2:
		padded += "=="
	case 3:
		padded += "="
	}
	padded = strings.ReplaceAll(padded, "-", "+")
	padded = strings.ReplaceAll(padded, "_", "/")

	msgBytes, err := base64.StdEncoding.DecodeString(padded)
	if err != nil {
		writeConnectError(w, "invalid_argument", "invalid base64 message", 400)
		return
	}

	var name string
	if encoding == "json" {
		var req map[string]string
		json.Unmarshal(msgBytes, &req)
		name = req["name"]
	} else {
		name = decodeStringField(msgBytes)
	}

	if name == "" {
		writeConnectError(w, "invalid_argument", "name is required", 400)
		return
	}

	message := fmt.Sprintf("Hello %s from connect-go", name)

	if encoding == "json" {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]string{"message": message})
	} else {
		w.Header().Set("Content-Type", "application/proto")
		w.Write(encodeStringField(message))
	}
}

func handleSayHelloStream(w http.ResponseWriter, r *http.Request) {
	// Read request envelope
	_, data, err := readStreamEnvelope(r.Body)
	if err != nil {
		writeConnectError(w, "internal", "failed to read request envelope", 500)
		return
	}
	name := decodeStringField(data)

	if name == "" {
		writeConnectError(w, "invalid_argument", "name is required", 400)
		return
	}

	w.Header().Set("Content-Type", "application/connect+proto")
	w.WriteHeader(200)
	flusher := w.(http.Flusher)

	for i := 1; i <= 3; i++ {
		msg := encodeStringField(fmt.Sprintf("Hello %s %d", name, i))
		writeStreamEnvelope(w, 0x00, msg)
		flusher.Flush()
	}

	writeEndStream(w)
	flusher.Flush()
}

func handleCollectHellos(w http.ResponseWriter, r *http.Request) {
	var names []string

	for {
		_, data, err := readStreamEnvelope(r.Body)
		if err != nil {
			break
		}
		name := decodeStringField(data)
		if name != "" {
			names = append(names, name)
		}
	}

	if len(names) == 0 {
		w.Header().Set("Content-Type", "application/connect+proto")
		w.WriteHeader(200)
		flusher := w.(http.Flusher)
		writeStreamError(w, "invalid_argument", "no names received")
		flusher.Flush()
		return
	}

	message := fmt.Sprintf("Hello %s", strings.Join(names, ", "))
	msg := encodeStringField(message)

	w.Header().Set("Content-Type", "application/connect+proto")
	w.WriteHeader(200)
	flusher := w.(http.Flusher)

	writeStreamEnvelope(w, 0x00, msg)
	flusher.Flush()
	writeEndStream(w)
	flusher.Flush()
}

func handleChat(w http.ResponseWriter, r *http.Request) {
	// Read all request messages first (half-duplex over HTTP/1.1)
	var messages []string
	for {
		_, data, err := readStreamEnvelope(r.Body)
		if err != nil {
			break
		}
		name := decodeStringField(data)
		if name != "" {
			messages = append(messages, name)
		}
	}

	w.Header().Set("Content-Type", "application/connect+proto")
	w.WriteHeader(200)
	flusher := w.(http.Flusher)

	for _, name := range messages {
		msg := encodeStringField(fmt.Sprintf("Hello %s", strings.ToUpper(name)))
		writeStreamEnvelope(w, 0x00, msg)
		flusher.Flush()
	}

	writeEndStream(w)
	flusher.Flush()
}
