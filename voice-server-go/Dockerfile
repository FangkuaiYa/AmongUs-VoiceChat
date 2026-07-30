# Build stage
FROM golang:1.26-alpine AS builder

WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -buildvcs=false -ldflags="-s -w" -o voice-server .

# Runtime stage
FROM alpine:3.21

RUN apk add --no-cache ca-certificates tzdata
WORKDIR /app
COPY --from=builder /app/voice-server .

EXPOSE 8000
ENV OPTIMAL_PLAYERS=0
ENV TURN_URL=""
ENV TURN_USER=""
ENV TURN_PASS=""
ENV MAX_BANDWIDTH_PER_CLIENT=0

ENTRYPOINT ["./voice-server"]
CMD ["-addr", "0.0.0.0:8000"]
