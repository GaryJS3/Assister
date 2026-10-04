FROM python:3.13-slim
WORKDIR /bridge
COPY bridge/esphome/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY bridge/satellite.proto .
RUN python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. satellite.proto
COPY bridge/esphome/main.py .
RUN useradd --system --uid 10001 bridge
USER bridge
CMD ["python", "main.py"]
