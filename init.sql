CREATE TABLE "Products" (
"Id" UUID NOT NULL,
"QuantityInStock" INTEGER NOT NULL,
CONSTRAINT "PK_Products" PRIMARY KEY ("Id")
);

CREATE TABLE "SubscriptionCheckpoints" (
"SubscriptionId" TEXT NOT NULL,
"Position" BIGINT NOT NULL,
CONSTRAINT "PK_SubscriptionCheckpoints" PRIMARY KEY ("SubscriptionId")
);

INSERT INTO "Products" ("Id", "QuantityInStock")
VALUES ('f1122ce7-4f70-46ad-8345-01bfb60beb21', 100);
INSERT INTO "Products" ("Id", "QuantityInStock")
VALUES ('a2345bde-1c8a-4bfe-9c3c-2e6a37213b19', 50);