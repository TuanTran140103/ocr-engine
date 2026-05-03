-- DATA STRUCTURE:
-- Key: "ocr:model:{modelId}:workers" (Hash)
-- Field: {workerId} -> Value: { "allowSlot": int, "used": int, "remainingPage": int, "TotalPage": int }
-- Field: "__config_total_max" -> Value: int (Global system concurrency)
--
-- Input:
-- KEYS[1]: model_workers_key
-- ARGV[1]: workerId
-- ARGV[2]: totalMaxConcurrency (int)
-- ARGV[3]: workerDataJson

local model_key = KEYS[1]
local worker_id = ARGV[1]
local total_max = tonumber(ARGV[2])
local worker_data_json = ARGV[3]

-- 1. Lưu cấu hình hệ thống vào Hash (Dùng field đặc biệt __config_total_max)
redis.call('HSET', model_key, '__config_total_max', total_max)

-- 2. Cập nhật/Thêm worker hiện tại
if worker_data_json and worker_data_json ~= "" then
    redis.call('HSET', model_key, worker_id, worker_data_json)
end

-- 3. Lấy tất cả dữ liệu để tính toán
local all_raw = redis.call('HGETALL', model_key)
local workers = {}

for i = 1, #all_raw, 2 do
    local field = all_raw[i]
    -- Bỏ qua field cấu hình, chỉ lấy worker data
    if field ~= "__config_total_max" then
        local w_data = cjson.decode(all_raw[i+1])
        w_data.id = field
        w_data.allowSlot = 0
        table.insert(workers, w_data)
    end
end

local num_workers = #workers
if num_workers == 0 then return nil end

local remaining_slots = total_max
local base_share = math.floor(total_max / num_workers)

-- Bước A: Chia đều
for _, w in ipairs(workers) do
    local take = math.min(w.remainingPage, base_share)
    w.allowSlot = take
    remaining_slots = remaining_slots - take
end

-- Bước B: Chia dư
while remaining_slots > 0 do
    table.sort(workers, function(a, b)
        local a_gap = a.remainingPage - a.allowSlot
        local b_gap = b.remainingPage - b.allowSlot
        if a_gap == b_gap then return a.remainingPage > b.remainingPage end
        return a_gap > b_gap
    end)

    local distributed = 0
    for _, w in ipairs(workers) do
        if remaining_slots > 0 and w.allowSlot < w.remainingPage then
            w.allowSlot = w.allowSlot + 1
            remaining_slots = remaining_slots - 1
            distributed = distributed + 1
        end
    end
    if distributed == 0 then break end
end

-- 4. Lưu kết quả
for _, w in ipairs(workers) do
    local id = w.id
    w.id = nil
    redis.call('HSET', model_key, id, cjson.encode(w))
end

return total_max
