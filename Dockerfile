FROM python:3.12-slim
 
WORKDIR /app
 
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
 
COPY app /app/app
 
VOLUME ["/data"]
 
ENV BEAST_HOST=readsb \
    BEAST_PORT=30005 \
    BEAST_OUTFILE=/data/beast.jsonl \
    BEAST_ROTATE=daily \
    BEAST_ROTATE_KEEP=14 \
    BEAST_STDOUT=0 \
    BEAST_NO_DECODE=0 \
    SNAPSHOT_INTERVAL=1.0 \
    PYTHONUNBUFFERED=1
 
EXPOSE 8080
 
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log"]
 
