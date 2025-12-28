#!/bin/bash
set -e

# Quick Start Script for Structured RAG
# This script starts all services using Docker Compose

echo "=========================================="
echo "Starting Structured RAG Application"
echo "=========================================="
echo ""

# Check if Docker is installed
if ! command -v docker &> /dev/null; then
    echo "Error: Docker is not installed. Please install Docker Desktop."
    exit 1
fi

# Check if Docker Compose is available
if ! docker compose version &> /dev/null; then
    echo "Error: Docker Compose is not available. Please update Docker Desktop."
    exit 1
fi

echo "Building and starting services..."
echo ""

# Start services
docker compose up --build

# Note: The script will keep running to show logs.
# Press Ctrl+C to stop all services.
