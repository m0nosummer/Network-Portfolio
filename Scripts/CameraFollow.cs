using UnityEngine;

namespace Arena.Gameplay
{
    public class CameraFollow : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform target;
        
        [Header("Camera Settings")]
        [SerializeField] private Vector3 offset = new Vector3(0, 13, -10);
        [SerializeField] private float smoothSpeed = 3f;
        [SerializeField] private bool useFixedOffset = true;
        
        [Header("Rotation Settings")]
        [SerializeField] private Vector3 fixedRotation = new Vector3(60f, 0f, 0f);
        
        void Start()
        {
            transform.rotation = Quaternion.Euler(fixedRotation);
        }
        
        void LateUpdate()
        {
            if (target == null) return;
            
            Vector3 desiredPosition = target.position + offset;
            
            if (useFixedOffset)
            {
                transform.position = desiredPosition;
            }
            else
            {
                // regardless of frame rate
                transform.position = Vector3.Lerp(
                    transform.position,
                    desiredPosition,
                    1f - Mathf.Exp(-smoothSpeed * Time.deltaTime)
                );
            }
            
            transform.rotation = Quaternion.Euler(fixedRotation);
        }
        
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            
            if (newTarget != null)
            {
                transform.position = newTarget.position + offset;
                transform.rotation = Quaternion.Euler(fixedRotation);
            }
        }
    }
}