-- DATA STRUCTURE:
-- Key: "ocr:model:{modelId}:workers" (Hash)
-- Field: {workerId} -> Value: JSON
-- Field: "__config_total_max" -> Value: int
--
-- Input:
-- KEYS[1]: model_workers_key
-- ARGV[1]: workerId

local model_key = KEYS[1]
local worker_id = ARGV[1]

-- 1. Tự đọc total_max từ Hash
local total_max_raw = redis.call('HGET', model_key, '__config_total_max')
if not total_max_raw then return nil end
local total_max = tonumber(total_max_raw)

-- 2. Lấy dữ liệu worker hiện tại
local raw = redis.call('HGET', model_key, worker_id)
if not raw then return nil end
local current_data = cjson.decode(raw)

-- 3. Cập nhật metrics
if current_data.used > 0 then current_data.used = current_data.used - 1 end
if current_data.remainingPage > 0 then current_data.remainingPage = current_data.remainingPage - 1 end

local needs_redistribution = current_data.remainingPage < current_data.allowSlot
redis.call('HSET', model_key, worker_id, cjson.encode(current_data))

if not needs_redistribution then return cjson.encode(current_data) end

-- 4. Tái phân bổ (Dùng giá trị total_max vừa đọc được)
local all_raw = redis.call('HGETALL', model_key)
local workers = {}
for i = 1, #all_raw, 2 do
    local field = all_raw[i]
    if field ~= "__config_total_max" then
        local w_data = cjson.decode(all_raw[i+1])
        w_data.id = field
        w_data.allowSlot = 0 
        table.insert(workers, w_data)
    end
end

local remaining_slots = total_max
local base_share = math.floor(total_max / #workers)
for _, w in ipairs(workers) do
    local take = math.min(w.remainingPage, base_share)
    w.allowSlot = take
    remaining_slots = remaining_slots - take
end

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

local updated_me = nil
for _, w in ipairs(workers) do
    local id = w.id
    if id == worker_id then updated_me = w end
    w.id = nil
    redis.call('HSET', model_key, id, cjson.encode(w))
end
return cjson.encode(updated_me)
