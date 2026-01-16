package service

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"sync"
	"time"

	deliveryv1 "warehouse/delivery/gen/go/delivery/v1"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/types/known/timestamppb"
)

type Service struct {
	deliveryv1.UnimplementedDeliveryServiceServer
	mu         sync.RWMutex
	deliveries []*deliveryv1.Delivery
}

func NewService() *Service {
	return &Service{
		deliveries: make([]*deliveryv1.Delivery, 0, 16),
	}
}

func (s *Service) CreateDelivery(ctx context.Context, req *deliveryv1.CreateDeliveryRequest) (*deliveryv1.CreateDeliveryResponse, error) {
	if req == nil {
		return nil, status.Error(codes.InvalidArgument, "request is required")
	}
	if err := req.ValidateAll(); err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}

	now := time.Now().UTC()
	delivery := &deliveryv1.Delivery{
		DeliveryId: newDeliveryID(),
		OrderId:    req.OrderId,
		Status:     deliveryv1.DeliveryStatus_DELIVERY_STATUS_PENDING,
		Address:    req.Address,
		Window:     req.Window,
		CreatedAt:  timestamppb.New(now),
	}

	s.mu.Lock()
	s.deliveries = append(s.deliveries, delivery)
	s.mu.Unlock()

	return &deliveryv1.CreateDeliveryResponse{Delivery: delivery}, nil
}

func (s *Service) ListDeliveries(ctx context.Context, _ *deliveryv1.ListDeliveriesRequest) (*deliveryv1.ListDeliveriesResponse, error) {
	s.mu.RLock()
	deliveries := make([]*deliveryv1.Delivery, len(s.deliveries))
	copy(deliveries, s.deliveries)
	s.mu.RUnlock()

	return &deliveryv1.ListDeliveriesResponse{Deliveries: deliveries}, nil
}

func newDeliveryID() string {
	buf := make([]byte, 16)
	if _, err := rand.Read(buf); err != nil {
		return hex.EncodeToString([]byte(time.Now().Format("20060102150405.000000000")))
	}
	return hex.EncodeToString(buf)
}
