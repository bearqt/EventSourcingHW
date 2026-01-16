FROM golang:1.24 AS build

WORKDIR /src
COPY go.mod ./
COPY go.sum ./
RUN go mod download

COPY cmd/ cmd/
COPY internal/ internal/
COPY gen/ gen/
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o /out/delivery-service ./cmd/delivery

FROM gcr.io/distroless/static:nonroot
WORKDIR /app
COPY --from=build /out/delivery-service /delivery-service
COPY --from=build /src/gen /app/gen
EXPOSE 8080 9090
USER nonroot:nonroot
ENTRYPOINT ["/delivery-service"]
