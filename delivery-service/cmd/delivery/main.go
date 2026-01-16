package main

import (
	"context"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	deliveryservice "warehouse/delivery/internal/service"
	deliveryv1 "warehouse/delivery/gen/go/delivery/v1"

	"github.com/grpc-ecosystem/grpc-gateway/v2/runtime"
	"google.golang.org/grpc"
)

func main() {
	grpcAddr := envOr("DELIVERY_GRPC_ADDR", ":9090")
	httpAddr := envOr("DELIVERY_HTTP_ADDR", ":8080")
	swaggerPath := envOr("DELIVERY_SWAGGER_PATH", "gen/openapiv2/delivery/v1/delivery.swagger.json")

	grpcLis, err := net.Listen("tcp", grpcAddr)
	if err != nil {
		log.Fatalf("grpc listen: %v", err)
	}

	grpcServer := grpc.NewServer()
	service := deliveryservice.NewService()
	deliveryv1.RegisterDeliveryServiceServer(grpcServer, service)

	gwMux := runtime.NewServeMux()
	if err := deliveryv1.RegisterDeliveryServiceHandlerServer(context.Background(), gwMux, service); err != nil {
		log.Fatalf("gateway register: %v", err)
	}

	httpMux := http.NewServeMux()
	httpMux.Handle("/v1/", gwMux)
	httpMux.HandleFunc("/swagger/swagger.json", swaggerJSONHandler(swaggerPath))
	httpMux.HandleFunc("/swagger/", swaggerUIHandler())
	httpMux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	httpServer := &http.Server{
		Addr:              httpAddr,
		Handler:           httpMux,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("grpc listening on %s", grpcAddr)
		if err := grpcServer.Serve(grpcLis); err != nil {
			log.Fatalf("grpc serve: %v", err)
		}
	}()

	go func() {
		log.Printf("http listening on %s", httpAddr)
		if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("http serve: %v", err)
		}
	}()

	waitForShutdown(grpcServer, httpServer)
}

func waitForShutdown(grpcServer *grpc.Server, httpServer *http.Server) {
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	<-sigCh

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	grpcServer.GracefulStop()
	if err := httpServer.Shutdown(ctx); err != nil {
		log.Printf("http shutdown: %v", err)
	}
}

func envOr(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}

func swaggerJSONHandler(path string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			w.WriteHeader(http.StatusMethodNotAllowed)
			return
		}
		http.ServeFile(w, r, path)
	}
}

func swaggerUIHandler() http.HandlerFunc {
	const html = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Delivery Service API</title>
    <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css" />
  </head>
  <body>
    <div id="swagger-ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
    <script>
      window.onload = () => {
        window.ui = SwaggerUIBundle({
          url: "/swagger/swagger.json",
          dom_id: "#swagger-ui",
        });
      };
    </script>
  </body>
</html>`

	return func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/swagger/" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write([]byte(html))
	}
}
